// MIT License - Copyright (c) 2025 BUCK Design LLC - https://github.com/buck-co

using System;
using System.Globalization;
using UnityEngine;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text;

namespace Buck.SaveAsync
{
    public class FileHandler : ScriptableObject
    {
        /// <summary>
        /// Stores the persistent data path for later use, which can only be accessed on the main thread.
        /// </summary>
        protected string m_persistentDataPath;

        // The encoding File.WriteAllText uses (UTF-8 without a BOM), so files stay byte-for-byte
        // identical to what earlier versions wrote.
        static readonly UTF8Encoding s_utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        const string k_tempFileSuffix = ".tmp";
        const int k_fileOperationAttempts = 5;
        const int k_fileOperationRetryDelayMs = 20;

        /// <summary>
        /// The suffix to append to the filename. By default, it is "_editor" when in the Unity Editor, and an empty string in builds.
        /// </summary>
        protected virtual string FilenameSuffix
#if UNITY_EDITOR
            => "_editor";
#else
            => string.Empty;
#endif

        /// <summary>
        /// The file extension to use for the files. By default, it is ".dat".
        /// </summary>
        protected virtual string FileExtension => ".dat";

        protected virtual void OnEnable()
            => m_persistentDataPath = Application.persistentDataPath;

        /// <summary>
        /// Validates that the given path or filename is safe and well-formed.
        /// Throws an exception if the path contains invalid characters, is absolute, or attempts directory traversal.
        /// </summary>
        /// <param name="pathOrFilename">The path or filename to validate.</param>
        /// <exception cref="ArgumentException">Thrown when the path is null, empty, whitespace, contains "..", is absolute, or contains invalid characters.</exception>
        protected virtual void ValidatePath(string pathOrFilename)
        {
            if (string.IsNullOrWhiteSpace(pathOrFilename))
                throw new ArgumentException("[Save Async] FileHandler.ValidatePath() - Path or filename cannot be null, empty, or whitespace.", nameof(pathOrFilename));

            if (pathOrFilename.Contains("..") || Path.IsPathRooted(pathOrFilename))
                throw new ArgumentException("[Save Async] FileHandler.ValidatePath() - Path contains invalid characters or is absolute.", nameof(pathOrFilename));

            if (pathOrFilename.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new ArgumentException("[Save Async] FileHandler.ValidatePath() - Path contains invalid characters.", nameof(pathOrFilename));
        }

        /// <summary>
        /// Returns the path to a file using the given path or filename and appends the <see cref="FilenameSuffix"/> and 
        /// <see cref="FileExtension"/> but does not include the persistent data path.
        /// For the full path, use <see cref="GetFullPath(string)"/>.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="pathOrFilename">The path or filename of the file that will be combined with the persistent data path.</param>
        protected string GetPartialPath(string pathOrFilename)
        {
            ValidatePath(pathOrFilename);

            string path = $"{pathOrFilename}{FilenameSuffix}{FileExtension}";

            var scope = SaveManager.ResolveScopeFor(pathOrFilename);
            
            if (scope == StorageScope.Slot)
            {
                if (SaveManager.SaveSlotIndex < 0)
                    throw new InvalidOperationException(
                        "[Save Async] Slot-scoped file requested but SaveSlotIndex is not set. " +
                        "Set SaveManager.SaveSlotIndex before saving/loading slot-scoped files.");

                return Path.Combine($"slot{SaveManager.SaveSlotIndex}", path);
            }

            // If the scope is global, just return the path as-is
            return path;
        }

        /// <summary>
        /// Returns the full path to a file in the persistent data path using the given path or filename.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="pathOrFilename">The path or filename of the file that will be combined with the persistent data path.</param>
        protected virtual string GetFullPath(string pathOrFilename)
            => Path.Combine(m_persistentDataPath, GetPartialPath(pathOrFilename));

        /// <summary>
        /// Returns true if a file exists at the given path or filename.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="pathOrFilename">The path or filename of the file to check.</param>
        /// <returns>True if the file exists; otherwise, false.</returns>
        public virtual bool Exists(string pathOrFilename)
            => File.Exists(GetFullPath(pathOrFilename));

        /// <summary>
        /// Writes the given content to a file at the given path or filename.
        /// The write is atomic: the content goes to a temporary file beside the target, is flushed to
        /// disk, and then replaces the target, so the file on disk is always either the complete
        /// previous version or the complete new one, even if the app is killed mid-write. The bytes
        /// written are the same as before (UTF-8 without a BOM).
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="pathOrFilename">The path or filename of the file to write.</param>
        /// <param name="content">The string to write to the file.</param>
        /// <param name="cancellationToken">Checked before the write starts. A write that has started always runs to completion.</param>
        public virtual async Task WriteFile(string pathOrFilename, string content, CancellationToken cancellationToken)
        {
            string fullPath = GetFullPath(pathOrFilename);
    
            string directoryPath = Path.GetDirectoryName(fullPath);
    
            if (!string.IsNullOrEmpty(directoryPath))
                Directory.CreateDirectory(directoryPath);

            cancellationToken.ThrowIfCancellationRequested();

            await Task.Run(() => WriteFileAtomically(fullPath, content), CancellationToken.None).ConfigureAwait(false);
        }

        static void WriteFileAtomically(string fullPath, string content)
        {
            byte[] bytes = s_utf8NoBom.GetBytes(content ?? string.Empty);
            string tempPath = fullPath + k_tempFileSuffix;
            bool tempFileComplete = false;

            try
            {
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                tempFileComplete = true;

                RetryWhileLocked(() =>
                {
                    if (File.Exists(fullPath))
                        File.Replace(tempPath, fullPath, null, ignoreMetadataErrors: true);
                    else
                        File.Move(tempPath, fullPath);
                });
            }
            catch
            {
                // A complete temporary file is kept only when the swap removed the target without
                // moving the new file into place: it is then the only copy of the data.
                if (!tempFileComplete || File.Exists(fullPath))
                    TryDelete(tempPath);

                throw;
            }
        }

        // Antivirus scanners and search indexers often hold a file open for a moment right after it
        // is written or read, which fails a rename or replace with a sharing violation. A few short
        // retries ride that out.
        static void RetryWhileLocked(Action fileOperation)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    fileOperation();
                    return;
                }
                catch (Exception e) when (attempt < k_fileOperationAttempts && (e is IOException || e is UnauthorizedAccessException))
                {
                    Thread.Sleep(k_fileOperationRetryDelayMs * attempt);
                }
            }
        }

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // Best effort: a leftover temporary file is overwritten by the next write.
            }
        }

        /// <summary>
        /// Writes the given content to a file at the given path or filename.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="pathOrFilename">The path or filename of the file to write.</param>
        /// <param name="content">The string to write to the file.</param>
        public virtual async Task WriteFile(string pathOrFilename, string content)
            => await WriteFile(pathOrFilename, content, CancellationToken.None);

        /// <summary>
        /// Returns the contents of a file at the given path or filename.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="pathOrFilename">The path or filename of the file to read.</param>
        /// <param name="cancellationToken">The cancellation token should be the same one from the calling MonoBehaviour.</param>
        public virtual async Task<string> ReadFile(string pathOrFilename, CancellationToken cancellationToken)
        {
            string fullPath = GetFullPath(pathOrFilename);

            if (!File.Exists(fullPath))
            {
                Debug.LogWarning($"[Save Async] FileHandler.ReadFile() - File does not exist at path \"{fullPath}\". This may be expected if the file has not been created yet.");
                return string.Empty;
            }

            string fileContent = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(fileContent))
            {
                if (SaveManager.SaveSlotIndex > -1)
                    Debug.LogWarning($"[Save Async] FileHandler.ReadFile() - The file \"{pathOrFilename}\" in slot index {SaveManager.SaveSlotIndex} was empty. This may be expected if the file has been erased.");
                else
                    Debug.LogWarning($"[Save Async] FileHandler.ReadFile() - The file \"{pathOrFilename}\" was empty. This may be expected if the file has been erased.");

                return string.Empty;
            }

            return fileContent;
        }

        /// <summary>
        /// Returns the contents of a file at the given path or filename.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="pathOrFilename">The path or filename of the file to read.</param>
        public virtual async Task<string> ReadFile(string pathOrFilename)
            => await ReadFile(pathOrFilename, CancellationToken.None);

        /// <summary>
        /// Erases a file at the given path or filename. The file will still exist on disk, but it will be empty.
        /// Use <see cref="Delete(string)"/> to remove the file from disk.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="pathOrFilename">The path or filename of the file to erase.</param>
        /// <param name="cancellationToken">The cancellation token should be the same one from the calling MonoBehaviour.</param>
        public virtual async Task Erase(string pathOrFilename, CancellationToken cancellationToken)
            => await WriteFile(pathOrFilename, string.Empty, cancellationToken);

        /// <summary>
        /// Erases a file at the given path or filename. The file will still exist on disk, but it will be empty.
        /// Use <see cref="Delete(string)"/> to remove the file from disk.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="pathOrFilename">The path or filename of the file to erase.</param>
        public virtual async Task Erase(string pathOrFilename)
            => await Erase(pathOrFilename, CancellationToken.None);

        /// <summary>
        /// Deletes a file at the given path or filename. This will remove the file from disk.
        /// Use <see cref="Erase(string, CancellationToken)"/> to fill the file with an empty string without removing it from disk.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="pathOrFilename">The path or filename of the file to delete.</param>
        /// <param name="cancellationToken">The cancellation token should be the same one from the calling MonoBehaviour.</param>
        public virtual async Task Delete(string pathOrFilename, CancellationToken cancellationToken)
        {
            string fullPath = GetFullPath(pathOrFilename);

            if (!File.Exists(fullPath))
                return;

            try
            {
                await Task.Run(() => File.Delete(fullPath), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.LogError($"[Save Async] FileHandler.Delete() - Access denied to file \"{pathOrFilename}\": {ex.Message}");
                throw;
            }
            catch (IOException ex)
            {
                Debug.LogError($"[Save Async] FileHandler.Delete() - IO error deleting file \"{pathOrFilename}\": {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Deletes a file at the given path or filename. This will remove the file from disk.
        /// Use <see cref="Erase(string, CancellationToken)"/> to fill the file with an empty string without removing it from disk.
        /// </summary>
        /// <param name="pathOrFilename">The path or filename of the file to delete.</param>
        public virtual async Task Delete(string pathOrFilename)
            => await Delete(pathOrFilename, CancellationToken.None);

        /// <summary>
        /// Moves an unreadable file aside so nothing ever overwrites it: renames it to
        /// "[file].corrupt-[UTC timestamp]" in the same folder, numbered if that name is taken.
        /// Returns the new full path, or null if there was no file to move. SaveManager calls this
        /// when a file cannot be parsed, before that file's ISaveables fall back to default state.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="pathOrFilename">The path or filename of the file to quarantine.</param>
        /// <param name="cancellationToken">The cancellation token should be the same one from the calling MonoBehaviour.</param>
        public virtual async Task<string> Quarantine(string pathOrFilename, CancellationToken cancellationToken)
        {
            string fullPath = GetFullPath(pathOrFilename);
            return await Task.Run(() => QuarantineFile(fullPath), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Moves an unreadable file aside so nothing ever overwrites it: renames it to
        /// "[file].corrupt-[UTC timestamp]" in the same folder, numbered if that name is taken.
        /// Returns the new full path, or null if there was no file to move.
        /// </summary>
        /// <param name="pathOrFilename">The path or filename of the file to quarantine.</param>
        public virtual async Task<string> Quarantine(string pathOrFilename)
            => await Quarantine(pathOrFilename, CancellationToken.None);

        static string QuarantineFile(string fullPath)
        {
            if (!File.Exists(fullPath))
                return null;

            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            string quarantinePath = $"{fullPath}.corrupt-{timestamp}";
            for (int n = 2; File.Exists(quarantinePath); n++)
                quarantinePath = $"{fullPath}.corrupt-{timestamp}-{n}";

            RetryWhileLocked(() => File.Move(fullPath, quarantinePath));
            return quarantinePath;
        }
    }
}
