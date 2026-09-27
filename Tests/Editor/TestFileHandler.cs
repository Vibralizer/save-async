// MIT License - Copyright (c) 2025 BUCK Design LLC - https://github.com/buck-co

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Buck.SaveAsync.Tests
{
    /// <summary>
    /// A FileHandler for tests. It keeps every file in a private temporary folder (never the real
    /// persistent data path), can hold writes in flight until a test releases them, and can make
    /// every write to one file fail. Everything else is the real FileHandler.
    /// </summary>
    public sealed class TestFileHandler : FileHandler
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "SaveAsyncTests-" + Guid.NewGuid().ToString("N"));

        TaskCompletionSource<bool> m_gate;
        TaskCompletionSource<bool> m_writeStarted = NewSignal();
        volatile string m_failingFile;

        static TaskCompletionSource<bool> NewSignal()
            => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The full path the given file is written to.</summary>
        public string PathFor(string filename) => GetFullPath(filename);

        protected override string GetFullPath(string pathOrFilename)
            => Path.Combine(Folder, pathOrFilename + FileExtension);

        /// <summary>Completes once a write has started while writes are held.</summary>
        public Task WriteStarted => m_writeStarted.Task;

        /// <summary>Every write from now on waits, after it has started, until <see cref="ReleaseWrites"/>.</summary>
        public void HoldWrites()
        {
            m_writeStarted = NewSignal();
            m_gate = NewSignal();
        }

        public void ReleaseWrites() => m_gate?.TrySetResult(true);

        /// <summary>Every write to this file throws an IOException.</summary>
        public void FailWritesTo(string filename) => m_failingFile = filename;

        public void DeleteFolder()
        {
            if (Directory.Exists(Folder))
                Directory.Delete(Folder, recursive: true);
        }

        public override async Task WriteFile(string pathOrFilename, string content, CancellationToken cancellationToken)
        {
            var gate = m_gate;
            if (gate != null)
            {
                m_writeStarted.TrySetResult(true);
                await gate.Task.ConfigureAwait(false);
            }

            if (pathOrFilename == m_failingFile)
                throw new IOException($"Simulated write failure for \"{pathOrFilename}\".");

            await base.WriteFile(pathOrFilename, content, cancellationToken).ConfigureAwait(false);
        }
    }
}
