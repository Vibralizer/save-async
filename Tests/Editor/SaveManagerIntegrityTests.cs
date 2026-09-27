// MIT License - Copyright (c) 2025 BUCK Design LLC - https://github.com/buck-co

using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Buck.SaveAsync.Tests
{
    /// <summary>
    /// The data-integrity guarantees of SaveManager and FileHandler: a torn file is quarantined
    /// instead of silently reset, Save() is a real durability barrier, one failing operation does
    /// not strand the ones queued behind it, a write in flight finishes when the app starts
    /// quitting, and writes are atomic without changing the bytes on disk. Every file lives in a
    /// private temporary folder.
    /// </summary>
    public class SaveManagerIntegrityTests
    {
        const int k_timeoutMs = 30000;

        [Serializable]
        public struct TestState
        {
            public int Value;
            public string Text;
        }

        sealed class TestSaveable : ISaveable<TestState>
        {
            public TestSaveable(string filename) => Filename = filename;

            public TestState State;

            public string Key => "Test." + Filename;
            public string Filename { get; }
            public StorageScope Scope => StorageScope.Global;
            public int Version => 0;
            public TestState CaptureState() => State;
            public void RestoreState(TestState state) => State = state;
        }

        GameObject m_host;
        TestFileHandler m_files;
        CancellationTokenSource m_quit;
        Func<CancellationToken> m_realExitToken;

        [SetUp]
        public void SetUp()
        {
            // Singleton<SaveManager> latches "shutting down" when a play session ends in this domain.
            typeof(Singleton<SaveManager>)
                .GetField("m_ShuttingDown", BindingFlags.NonPublic | BindingFlags.Static)
                ?.SetValue(null, false);

            m_host = new GameObject(nameof(SaveManagerIntegrityTests));
            m_host.AddComponent<SaveManager>();
            m_files = ScriptableObject.CreateInstance<TestFileHandler>();

            // Stand in for Application.exitCancellationToken so a test can start a quit.
            m_quit = new CancellationTokenSource();
            m_realExitToken = SaveManager.ExitCancellationToken;
            SaveManager.ExitCancellationToken = () => m_quit.Token;

            SaveManager.ResetForTests(m_files);
        }

        [TearDown]
        public async Task TearDown()
        {
            // Never leave a drain loop blocked on a held write for the next test.
            m_files.ReleaseWrites();
            for (int i = 0; i < 100 && SaveManager.IsBusy; i++)
                await Task.Delay(50);

            SaveManager.ResetForTests(null);
            SaveManager.ExitCancellationToken = m_realExitToken;
            m_quit.Dispose();

            Object.DestroyImmediate(m_host);
            m_files.DeleteFolder();
            Object.DestroyImmediate(m_files);
        }

        TestSaveable Register(string filename)
        {
            var saveable = new TestSaveable(filename);
            SaveManager.RegisterSaveable(saveable);
            return saveable;
        }

        string ReadSaved(string filename) => File.ReadAllText(m_files.PathFor(filename));

        [Test, Timeout(k_timeoutMs)]
        public async Task TornFile_IsQuarantined_NotSilentlyReset_AndNeverOverwritten()
        {
            var settings = Register("Settings");
            settings.State = new TestState { Value = 42, Text = "the user's data" };
            await SaveManager.Save("Settings");

            // Tear the file the way an interrupted write did: only the first half reached the disk.
            string path = m_files.PathFor("Settings");
            byte[] saved = File.ReadAllBytes(path);
            byte[] torn = new byte[saved.Length / 2];
            Array.Copy(saved, torn, torn.Length);
            File.WriteAllBytes(path, torn);

            LogAssert.Expect(LogType.Error, new Regex("is unreadable and was moved to"));
            await SaveManager.Load("Settings");

            // The saveable falls back to defaults, and the torn bytes are kept under a timestamped name.
            Assert.AreEqual(0, settings.State.Value);
            Assert.IsNull(settings.State.Text);
            Assert.IsFalse(File.Exists(path), "The torn file was left where the next save would overwrite it.");
            string[] quarantined = Directory.GetFiles(m_files.Folder, "Settings.dat.corrupt-*");
            Assert.AreEqual(1, quarantined.Length);
            CollectionAssert.AreEqual(torn, File.ReadAllBytes(quarantined[0]));

            // The next save writes a fresh file and never touches the quarantined copy.
            settings.State = new TestState { Value = 7 };
            await SaveManager.Save("Settings");
            StringAssert.Contains("\"Value\": 7", ReadSaved("Settings"));
            CollectionAssert.AreEqual(torn, File.ReadAllBytes(quarantined[0]));
        }

        [Test, Timeout(k_timeoutMs)]
        public async Task Save_WhileAnotherSaveIsInFlight_CompletesOnlyOnceItsOwnFileIsWritten()
        {
            var first = Register("First");
            var second = Register("Second");
            first.State = new TestState { Value = 1 };
            second.State = new TestState { Value = 2 };

            m_files.HoldWrites();
            Awaitable inFlight = SaveManager.Save("First");
            await m_files.WriteStarted;

            Awaitable barrier = SaveManager.Save("Second");
            await Task.Delay(100);
            Assert.IsFalse(barrier.IsCompleted, "Save() completed while its file was still unwritten.");

            m_files.ReleaseWrites();
            await barrier;
            StringAssert.Contains("\"Value\": 2", ReadSaved("Second"));

            await inFlight;
            StringAssert.Contains("\"Value\": 1", ReadSaved("First"));
        }

        [Test, Timeout(k_timeoutMs)]
        public async Task FailingOperation_DoesNotStrandTheOperationsQueuedBehindIt()
        {
            Register("First");
            Register("Broken");
            var last = Register("Last");
            last.State = new TestState { Value = 3 };

            m_files.FailWritesTo("Broken");
            m_files.HoldWrites();
            Awaitable inFlight = SaveManager.Save("First");
            await m_files.WriteStarted;

            Awaitable failing = SaveManager.Save("Broken");
            Awaitable queuedBehind = SaveManager.Save("Last");

            // The simulated failure is logged as an error on purpose.
            LogAssert.ignoreFailingMessages = true;
            m_files.ReleaseWrites();
            await inFlight;

            Exception failure = null;
            try
            {
                await failing;
            }
            catch (Exception e)
            {
                failure = e;
            }

            Assert.IsInstanceOf<IOException>(failure, "The failure should reach the caller whose save failed.");

            await queuedBehind;
            StringAssert.Contains("\"Value\": 3", ReadSaved("Last"));
        }

        [Test, Timeout(k_timeoutMs)]
        public async Task QuitDuringAWrite_LetsTheWriteFinish()
        {
            var settings = Register("Settings");
            settings.State = new TestState { Value = 1 };
            await SaveManager.Save("Settings");

            settings.State = new TestState { Value = 2 };
            m_files.HoldWrites();
            Awaitable save = SaveManager.Save("Settings");
            await m_files.WriteStarted;

            // The app starts quitting while the write is in flight.
            m_quit.Cancel();
            m_files.ReleaseWrites();
            await save;

            StringAssert.Contains("\"Value\": 2", ReadSaved("Settings"));
            Assert.IsFalse(File.Exists(m_files.PathFor("Settings") + ".tmp"));
        }

        [Test, Timeout(k_timeoutMs)]
        public async Task WriteThatFailsPartway_LeavesThePreviousFileByteIdentical()
        {
            await m_files.WriteFile("Settings", "[ \"the previous, complete save\" ]");
            string path = m_files.PathFor("Settings");
            byte[] before = File.ReadAllBytes(path);

            // Long enough to span many write buffers, ending in a lone surrogate the UTF-8 encoder
            // rejects: the write fails after nearly all of the content would have streamed out.
            string failsAtTheEnd = new string('x', 64 * 1024) + "\uD800";
            Exception failure = null;
            try
            {
                await m_files.WriteFile("Settings", failsAtTheEnd);
            }
            catch (Exception e)
            {
                failure = e;
            }

            Assert.IsNotNull(failure);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }

        [Test, Timeout(k_timeoutMs)]
        public async Task WriteFile_WritesTheSameBytesAsBefore()
        {
            const string content = "[\n  {\n    \"Key\": \"Settings\",\n    \"Data\": \"café ♫ \U0001F3B5\"\n  }\n]";
            await m_files.WriteFile("Format", content);

            // What SaveAsync 0.14 wrote: File.WriteAllTextAsync straight onto the path.
            Directory.CreateDirectory(m_files.Folder);
            string legacyPath = Path.Combine(m_files.Folder, "Legacy.dat");
            await File.WriteAllTextAsync(legacyPath, content);

            CollectionAssert.AreEqual(File.ReadAllBytes(legacyPath), File.ReadAllBytes(m_files.PathFor("Format")));
            Assert.IsFalse(File.Exists(m_files.PathFor("Format") + ".tmp"));
        }
    }
}
