// MIT License - Copyright (c) 2025 BUCK Design LLC - https://github.com/buck-co

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Buck.SaveAsync
{
    [AddComponentMenu("SaveAsync/SaveManager")]
    public class SaveManager : Singleton<SaveManager>
    {
        [SerializeField, Tooltip("Background threads are still an experimental feature and are turned off by default. " +
                                 "They do increase performance in many instances, but exceptions on a background thread " +
                                 "may not be caught and logged in Unity, and methods might fail silently. Use with caution!")]
        bool m_useBackgroundThread = false;

        [SerializeField, Tooltip("Enables encryption for save data. " +
                                 "XOR encryption is basic but extremely fast. Support for AES encryption is planned." +
                                 "Do not change the encryption type once the game has shipped!")]
        EncryptionType m_encryptionType = EncryptionType.None;

        [SerializeField, Tooltip(
             "The password used to encrypt and decrypt save data. This password should be unique to your game. " +
             "Do not change the encryption password once the game has shipped!")]
        string m_encryptionPassword = "password";

        [SerializeField, Tooltip(
             "This field can be left blank. SaveAsync allows the FileHandler class to be overridden." +
             "This can be useful in scenarios where files should not be saved using local file IO" +
             "(such as cloud saves) or when a platform-specific save API must be used. " +
             "If you want to use a custom file handler, create a new class that inherits from FileHandler and assign it here.")]
        FileHandler m_customFileHandler;

        enum FileOperationType
        {
            Save,
            Load,
            Delete,
            Erase,
            LoadDefaults
        }

        sealed class FileOperation
        {
            public readonly FileOperationType Type;
            public readonly string[] Filenames;
            public readonly OperationContext Context;

            // Resolved when THIS operation has finished (or failed), whichever caller's drain loop
            // ran it. Awaiting it is what makes Save() a real durability barrier.
            public readonly TaskCompletionSource<bool> Completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public FileOperation(FileOperationType operationType, string[] filenames, OperationContext context)
            {
                Type = operationType;
                Filenames = filenames;
                Context = context;
            }
        }

        struct OperationContext
        {
            public bool UseBackgroundThread;
            public EncryptionType EncryptionType;
            public string EncryptionPassword;
            public CancellationToken CancellationToken;
        }

        interface IBoxedSaveable
        {
            string Key { get; }
            string Filename { get; }
            Type StateType { get; }
            int Version { get; }
            object CaptureStateBoxed();
            void RestoreStateBoxed(object state);
        }

        sealed class BoxedSaveable<TState> : IBoxedSaveable
        {
            readonly ISaveable<TState> m_inner;

            public BoxedSaveable(ISaveable<TState> inner) => m_inner = inner;

            public string Key => m_inner.Key;
            public string Filename => m_inner.Filename;
            public Type StateType => typeof(TState);
            public int Version => m_inner.Version;

            public object CaptureStateBoxed() => m_inner.CaptureState();

            public void RestoreStateBoxed(object state)
            {
                if (state is null)
                {
                    m_inner.RestoreState(default);
                    return;
                }

                m_inner.RestoreState((TState)state);
            }
        }

        sealed class LoadedSaveable
        {
            public string Key;
            public int EntryVersion;
            public JToken Data;
        }

        static FileHandler m_fileHandler;

        static readonly Dictionary<string, IBoxedSaveable> m_saveables = new();
        static readonly Queue<FileOperation> m_fileOperationQueue = new();
        static readonly Dictionary<string, StorageScope> s_fileScopes = new();

        static readonly object s_QueueLock = new();
        static bool m_initialized;

        static int s_MainThreadId;

        // The quit signal every operation is linked to. The package's EditMode tests swap in their
        // own token to simulate a quit, because Unity raises Application.exitCancellationToken only
        // when play mode exits or the player quits.
        internal static Func<CancellationToken> ExitCancellationToken = () => Application.exitCancellationToken;

        static readonly JsonSerializerSettings s_jsonNoTypes = new()
        {
            Formatting = Formatting.Indented,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            TypeNameHandling = TypeNameHandling.Auto
        };

        static bool IsMainThread => Environment.CurrentManagedThreadId == s_MainThreadId;

        void Awake()
        {
            s_MainThreadId = Environment.CurrentManagedThreadId;
            Initialize();
        }

        static void Initialize()
        {
            if (m_initialized)
                return;

            m_fileHandler = Instance != null && Instance.m_customFileHandler != null
                ? Instance.m_customFileHandler
                : ScriptableObject.CreateInstance<FileHandler>();

            m_initialized = true;
        }

        #region SaveAsync API

        /// <summary>
        /// Boolean indicating whether a file operation is queued or in progress.
        /// </summary>
        public static bool IsBusy { get; private set; }

        /// <summary>
        /// Stores the current save slot index, which can be used to determine which save slot to use for saving and loading files.
        /// A value of -1 indicates that no save slot is being used, which can be useful for settings files or other data that does not require a save slot.
        /// </summary>
        public static int SaveSlotIndex { get; set; } = -1;

        /// <summary>
        /// Registers an ISaveable and its file for saving and loading.
        /// </summary>
        /// <typeparam name="TState">The serializable state type for this saveable.</typeparam>
        /// <param name="saveable">The ISaveable to register for saving and loading.</param>
        public static void RegisterSaveable<TState>(ISaveable<TState> saveable)
        {
            Initialize();

            if (saveable == null)
            {
                Debug.LogWarning("[Save Async] SaveManager.RegisterSaveable() - Attempted to register a null ISaveable.");
                return;
            }

            var boxed = new BoxedSaveable<TState>(saveable);
            if (!m_saveables.TryAdd(boxed.Key, boxed))
                Debug.LogWarning($"[Save Async] SaveManager.RegisterSaveable() - Saveable with Key \"{boxed.Key}\" already exists.");
            
            var scope = saveable.Scope;
            if (s_fileScopes.TryGetValue(boxed.Filename, out var existing) && existing != scope)
                Debug.LogError($"[Save Async] Conflicting scopes for filename \"{boxed.Filename}\": {existing} vs {scope}.");
            else
                s_fileScopes[boxed.Filename] = scope;
        }
        
        internal static StorageScope ResolveScopeFor(string filename)
        {
            if (string.IsNullOrEmpty(filename))
                return StorageScope.Slot; // safest default

            return s_fileScopes.TryGetValue(filename, out var scope)
                ? scope
                : StorageScope.Slot; // default Slot unless explicitly registered as Global
        }

        /// <summary>
        /// Checks if a file exists at the given path or filename.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="filename">The path or filename to check for existence.</param>
        public static bool Exists(string filename)
        {
            Initialize();
            return m_fileHandler.Exists(filename);
        }

        /// <summary>
        /// Saves the files at the given paths or filenames. The returned Awaitable completes only
        /// once these files are on disk, even when other file operations were queued ahead of this
        /// one, so awaiting it is a durability barrier (for example before quitting). Awaiting it
        /// throws if the save failed.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="filenames">The array of paths or filenames to save.</param>
        public static async Awaitable Save(string[] filenames)
        {
            Initialize();
            var ctx = CreateContext();
            if (ctx.CancellationToken.IsCancellationRequested)
                return;

            await DoFileOperation(FileOperationType.Save, filenames, ctx);
        }

        /// <summary>
        /// Saves the file at the given path or filename.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="filename">The path or filename to save.</param>
        public static async Awaitable Save(string filename)
            => await Save(new[] { filename });

        /// <summary>
        /// Loads the files at the given paths or filenames. The returned Awaitable completes once
        /// their ISaveables have been restored. A file that cannot be parsed (torn by an interrupted
        /// write, truncated, or corrupted) is moved aside with <see cref="FileHandler.Quarantine(string)"/>
        /// before its ISaveables fall back to default state, so a later save never overwrites it.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="filenames">The array of paths or filenames to load.</param>
        public static async Awaitable Load(string[] filenames)
        {
            Initialize();
            var ctx = CreateContext();
            if (ctx.CancellationToken.IsCancellationRequested)
                return;

            await DoFileOperation(FileOperationType.Load, filenames, ctx);
        }

        /// <summary>
        /// Loads the file at the given path or filename.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="filename">The path or filename to load.</param>
        public static async Awaitable Load(string filename)
            => await Load(new[] { filename });

        /// <summary>
        /// Triggers loading without file I/O. Any saved files will be ignored and RestoreState() will be passed a null value.
        /// This can be useful if you want RestoreState() to use default values, such as when working in the Unity Editor
        /// where you may want to test default states without loading save data.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="filenames">The array of paths or filenames whose ISaveables should be reset to defaults.</param>
        public static async Awaitable LoadDefaults(string[] filenames)
        {
            Initialize();
            var ctx = CreateContext();
            if (ctx.CancellationToken.IsCancellationRequested)
                return;

            await DoFileOperation(FileOperationType.LoadDefaults, filenames, ctx);
        }

        /// <summary>
        /// Triggers loading without file I/O. Any saved files will be ignored and RestoreState() will be passed a null value.
        /// This can be useful if you want RestoreState() to use default values, such as when working in the Unity Editor
        /// where you may want to test default states without loading save data.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="filename">The path or filename whose ISaveables should be reset to defaults.</param>
        public static async Awaitable LoadDefaults(string filename)
            => await LoadDefaults(new[] { filename });

        /// <summary>
        /// Deletes the files at the given paths or filenames. Each file will be removed from disk.
        /// Use <see cref="Erase(string[])"/> to fill each file with an empty string without removing it from disk.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="filenames">The array of paths or filenames to delete.</param>
        public static async Awaitable Delete(string[] filenames)
        {
            Initialize();
            var ctx = CreateContext();
            if (ctx.CancellationToken.IsCancellationRequested)
                return;

            await DoFileOperation(FileOperationType.Delete, filenames, ctx);
        }

        /// <summary>
        /// Deletes the file at the given path or filename. The file will be removed from disk.
        /// Use <see cref="Erase(string)"/> to fill the file with an empty string without removing it from disk.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="filename">The path or filename to delete.</param>
        public static async Awaitable Delete(string filename)
            => await Delete(new[] { filename });

        /// <summary>
        /// Erases the files at the given paths or filenames. Each file will still exist on disk, but it will be empty.
        /// Use <see cref="Delete(string[])"/> to remove the files from disk.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="filenames">The array of paths or filenames to erase.</param>
        public static async Awaitable Erase(string[] filenames)
        {
            Initialize();
            var ctx = CreateContext();
            if (ctx.CancellationToken.IsCancellationRequested)
                return;

            await DoFileOperation(FileOperationType.Erase, filenames, ctx);
        }

        /// <summary>
        /// Erases the file at the given path or filename. The file will still exist on disk, but it will be empty.
        /// Use <see cref="Delete(string)"/> to remove the file from disk.
        /// <code>
        /// File example: "MyFile"
        /// Path example: "MyFolder/MyFile"
        /// </code>
        /// </summary>
        /// <param name="filename">The path or filename to erase.</param>
        public static async Awaitable Erase(string filename)
            => await Erase(new[] { filename });

        /// <summary>
        /// Sets the given Guid byte array to a new Guid byte array if it is null, empty, or an empty Guid.
        /// This method can be useful for creating unique keys for ISaveables.
        /// </summary>
        /// <param name="guidBytes">The byte array (passed by reference) that you would like to fill with a serializable guid.</param>
        /// <returns>The same byte array that contains the serializable guid, but returned from the method.</returns>
        public static byte[] GetSerializableGuid(ref byte[] guidBytes)
        {
            if (guidBytes == null)
            {
                Debug.LogWarning("[Save Async] SaveManager.GetSerializableGuid() - Guid byte array is null. Generating a new Guid.");
                guidBytes = Guid.NewGuid().ToByteArray();
            }

            if (guidBytes.Length == 0)
            {
                Debug.LogWarning("[Save Async] SaveManager.GetSerializableGuid() - Guid byte array is empty. Generating a new Guid.");
                guidBytes = Guid.NewGuid().ToByteArray();
            }

            if (guidBytes.Length != 16)
                throw new ArgumentException("[Save Async] SaveManager.GetSerializableGuid() - Guid byte array must be 16 bytes long.");

            Guid guidObj = new Guid(guidBytes);

            if (guidObj == Guid.Empty)
            {
                Debug.LogWarning("[Save Async] SaveManager.GetSerializableGuid() - Guid is empty. Generating a new Guid.");
                guidBytes = Guid.NewGuid().ToByteArray();
            }

            return guidBytes;
        }

        #endregion

        // Test hook for the package's EditMode tests: forgets every registration and queued operation
        // and swaps in the given file handler (null restores the normal lazy initialization).
        internal static void ResetForTests(FileHandler fileHandler)
            => ResetStatics(fileHandler);

        // Every Play session starts from an empty SaveManager, with or without a domain reload
        // (Enter Play Mode Settings, and Unity's CoreCLR runtime). Without one, the saveables,
        // the file handler (a ScriptableObject Unity destroyed when the previous session ended)
        // and the initialized flag would otherwise carry over, and the first Save of the new
        // session would capture state from the previous session's destroyed objects.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetForPlaySession()
            => ResetStatics(null);

        static void ResetStatics(FileHandler fileHandler)
        {
            lock (s_QueueLock)
            {
                // Operations still queued belong to the previous session; release their awaiters.
                while (m_fileOperationQueue.Count > 0)
                    m_fileOperationQueue.Dequeue().Completion.TrySetCanceled();
                IsBusy = false;
            }

            m_saveables.Clear();
            s_fileScopes.Clear();
            SaveSlotIndex = -1;
            m_fileHandler = fileHandler;
            m_initialized = fileHandler != null;
        }

        static OperationContext CreateContext()
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(Instance.destroyCancellationToken, ExitCancellationToken()).Token;
            return new OperationContext
            {
                UseBackgroundThread = Instance && Instance.m_useBackgroundThread,
                EncryptionType = Instance ? Instance.m_encryptionType : EncryptionType.None,
                EncryptionPassword = Instance ? Instance.m_encryptionPassword : string.Empty,
                CancellationToken = linked
            };
        }

        static async Awaitable DoFileOperation(FileOperationType requestedType, string[] requestedFilenames, OperationContext ctx)
        {
            if (m_saveables.Count == 0)
            {
                Debug.LogError("[Save Async] SaveManager.DoFileOperation() - No saveables have been registered. " +
                         "Register ISaveable<TState> before using save, load, erase, or delete methods.");
                return;
            }

            var operation = new FileOperation(requestedType, requestedFilenames, ctx);
            bool startDrain;

            lock (s_QueueLock)
            {
                m_fileOperationQueue.Enqueue(operation);
                startDrain = !IsBusy;
                IsBusy = true;
            }

            // One drain loop at a time runs every queued operation in order, including the ones
            // enqueued while it runs, and clears IsBusy once the queue is empty.
            if (startDrain)
                _ = DrainQueueAsync();

            // Complete only when THIS operation has finished, even when another caller's drain loop
            // is the one running it: awaiting Save() is a real durability barrier. A failed
            // operation throws here, to its own caller.
            await operation.Completion.Task;
        }

        static async Awaitable DrainQueueAsync()
        {
            while (true)
            {
                FileOperation operation;

                lock (s_QueueLock)
                {
                    if (m_fileOperationQueue.Count == 0)
                    {
                        IsBusy = false;
                        return;
                    }

                    operation = m_fileOperationQueue.Dequeue();
                }

                try
                {
                    await RunOperationAsync(operation);
                    operation.Completion.TrySetResult(true);
                }
                catch (OperationCanceledException)
                {
                    operation.Completion.TrySetCanceled();
                }
                catch (Exception e)
                {
                    // A failure belongs to its own operation. The operations queued behind it still
                    // run instead of being stranded until some later call starts a new drain.
                    Debug.LogError($"[Save Async] SaveManager.DoFileOperation() - Exception: {e.Message}\n{e.StackTrace}");
                    operation.Completion.TrySetException(e);
                }
            }
        }

        static async Awaitable RunOperationAsync(FileOperation operation)
        {
            var ctx = operation.Context;

            // Every operation starts on the main thread, where ISaveable.CaptureState and
            // RestoreState run. The previous operation may have finished on a worker thread.
            await Awaitable.MainThreadAsync();

            // Nothing new starts once the app is quitting. An operation that has already started
            // is not cancelled partway (see SaveFileOperationAsync).
            if (ctx.CancellationToken.IsCancellationRequested)
                return;

            if (ctx.UseBackgroundThread)
                await Awaitable.BackgroundThreadAsync();

            switch (operation.Type)
            {
                case FileOperationType.Save:
                    await SaveFileOperationAsync(operation.Filenames, ctx);
                    break;

                case FileOperationType.Load:
                    var loaded = await LoadFileOperationAsync(operation.Filenames, ctx);
                    await Awaitable.MainThreadAsync();
                    RestorePass(new HashSet<string>(operation.Filenames), loaded, didLoad: true, didDefaults: false);
                    break;

                case FileOperationType.Delete:
                    await DeleteFileOperationAsync(operation.Filenames, eraseAndKeepFile: false, ctx);
                    break;

                case FileOperationType.Erase:
                    await DeleteFileOperationAsync(operation.Filenames, eraseAndKeepFile: true, ctx);
                    break;

                case FileOperationType.LoadDefaults:
                    await Awaitable.MainThreadAsync();
                    RestorePass(new HashSet<string>(operation.Filenames), null, didLoad: false, didDefaults: true);
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        static void RestorePass(HashSet<string> affectedFilenames, List<LoadedSaveable> loadedSaveables, bool didLoad, bool didDefaults)
        {
            var restoredSaveables = new Dictionary<string, bool>(m_saveables.Count);
            foreach (var kvp in m_saveables)
                restoredSaveables[kvp.Key] = false;

            if (didLoad && loadedSaveables != null && loadedSaveables.Count > 0)
            {
                foreach (var loaded in loadedSaveables)
                {
                    if (loaded.Key == null)
                    {
                        Debug.LogError("[Save Async] SaveManager.DoFileOperation() - The key for an ISaveable was null. JSON data may be malformed.");
                        continue;
                    }

                    if (!m_saveables.TryGetValue(loaded.Key, out var boxed) || boxed == null)
                    {
                        Debug.LogError($"[Save Async] SaveManager.DoFileOperation() - The ISaveable with the key \"{loaded.Key}\" was not found or is null. The data will not be restored.");
                        continue;
                    }

                    // Version check: if the on-disk entry's Version doesn't match the registered saveable's Version,
                    // skip old data and explicitly restore defaults for this saveable.
                    if (loaded.EntryVersion != boxed.Version)
                    {
                        Debug.LogWarning($"[Save Async] SaveManager.DoFileOperation() - Version mismatch for key \"{loaded.Key}\". " +
                                         $"Save data has Version {loaded.EntryVersion}; runtime expects {boxed.Version}. Defaults will be used.");
                        boxed.RestoreStateBoxed(null);
                        restoredSaveables[loaded.Key] = true;
                        continue;
                    }

                    try
                    {
                        object state = loaded.Data?.ToObject(boxed.StateType, JsonSerializer.CreateDefault(s_jsonNoTypes));
                        boxed.RestoreStateBoxed(state);
                        restoredSaveables[loaded.Key] = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Save Async] SaveManager.DoFileOperation() - Failed to restore state for key \"{loaded.Key}\": {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }

            foreach (var kvp in m_saveables)
            {
                if (restoredSaveables[kvp.Key])
                    continue;

                if (!affectedFilenames.Contains(kvp.Value.Filename))
                    continue;

                kvp.Value.RestoreStateBoxed(null);

                if (didLoad)
                {
                    Debug.LogWarning($"[Save Async] SaveManager.DoFileOperation() - The ISaveable with the key \"{kvp.Key}\" " +
                               "was not restored from save data. This could mean the save data did not contain any data for this ISaveable.");
                }
            }

            if (didDefaults)
                Debug.Log("[Save Async] SaveManager.DoFileOperation() - Saveables were loaded with default state because LoadDefaults() was called.");
        }

        static async Awaitable SaveFileOperationAsync(string[] filenames, OperationContext ctx)
        {
            var ct = ctx.CancellationToken;
            if (ct.IsCancellationRequested)
                return;

            List<ExceptionDispatchInfo> failures = null;

            foreach (string filename in filenames)
            {
                // Each file starts on the main thread (unless the background thread option is on):
                // capturing state and resolving the file's path touch Unity objects, and the
                // previous write may have resumed on a worker thread.
                if (!ctx.UseBackgroundThread)
                    await Awaitable.MainThreadAsync();

                try
                {
                    var toSave = new List<IBoxedSaveable>();
                    foreach (var s in m_saveables.Values)
                        if (s.Filename == filename)
                            toSave.Add(s);

                    string json = SaveablesToJson(toSave);
                    if (string.IsNullOrEmpty(json))
                        throw new InvalidOperationException($"[Save Async] SaveManager.SaveFileOperationAsync() - JSON serialization returned empty for file \"{filename}\".");

                    string encrypted = Encryption.Encrypt(json, ctx.EncryptionPassword, ctx.EncryptionType);

                    // Once started, a save runs to completion: cancelling a write partway at quit is
                    // how torn save files happened. FileHandler.WriteFile is atomic, so even a killed
                    // process leaves either the previous file or the new one on disk.
                    await m_fileHandler.WriteFile(filename, encrypted, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // One file failing does not stop the other files in this save.
                    Debug.LogError($"[Save Async] SaveManager.SaveFileOperationAsync() - Exception while saving \"{filename}\": {e.Message}\n{e.StackTrace}");
                    (failures ??= new List<ExceptionDispatchInfo>()).Add(ExceptionDispatchInfo.Capture(e));
                }
            }

            if (failures != null)
                failures[0].Throw();
        }

        static async Awaitable<List<LoadedSaveable>> LoadFileOperationAsync(string[] filenames, OperationContext ctx)
        {
            var ct = ctx.CancellationToken;
            var loadedSaveables = new List<LoadedSaveable>();

            try
            {
                foreach (string filename in filenames)
                {
                    // Resolving the file's path touches Unity objects in some file handlers, and the
                    // previous read may have resumed on a worker thread.
                    if (!ctx.UseBackgroundThread)
                        await Awaitable.MainThreadAsync();

                    string fileContent = await m_fileHandler.ReadFile(filename, ct).ConfigureAwait(false);

                    if (string.IsNullOrEmpty(fileContent))
                        continue;

                    if (TryParseEntries(fileContent, ctx, loadedSaveables, out var parseError))
                        continue;

                    // Unreadable: torn by an interrupted write, truncated, or corrupted. Move it aside
                    // under a timestamped name BEFORE its ISaveables fall back to defaults, so the
                    // next save can never overwrite whatever it still holds.
                    if (!ctx.UseBackgroundThread)
                        await Awaitable.MainThreadAsync();

                    string quarantinedPath = await m_fileHandler.Quarantine(filename, CancellationToken.None).ConfigureAwait(false);
                    Debug.LogError($"[Save Async] SaveManager.LoadFileOperationAsync() - The file \"{filename}\" is unreadable and was moved to \"{quarantinedPath}\". " +
                                   $"Its ISaveables will use default state. Error: {parseError.Message}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Save Async] SaveManager.LoadFileOperationAsync() - Exception: {e.Message}\n{e.StackTrace}");
                throw;
            }

            return loadedSaveables;
        }

        // Decrypts and parses a whole file, and adds its entries only if ALL of it parsed, so a
        // damaged file never half-restores.
        static bool TryParseEntries(string fileContent, OperationContext ctx, List<LoadedSaveable> loadedSaveables, out Exception error)
        {
            try
            {
                string json = Encryption.Decrypt(fileContent, ctx.EncryptionPassword, ctx.EncryptionType);
                var array = JArray.Parse(json);
                var entries = new List<LoadedSaveable>(array.Count);

                foreach (var item in array)
                {
                    var key = item["Key"]?.ToString();
                    int entryVersion = item["Version"]?.Value<int?>() ?? 0; // legacy entries will be 0
                    var data = item["Data"];
                    entries.Add(new LoadedSaveable
                    {
                        Key = key,
                        EntryVersion = entryVersion,
                        Data = data
                    });
                }

                loadedSaveables.AddRange(entries);
                error = null;
                return true;
            }
            catch (Exception e)
            {
                error = e;
                return false;
            }
        }

        static async Awaitable DeleteFileOperationAsync(string[] filenames, bool eraseAndKeepFile, OperationContext ctx)
        {
            var ct = ctx.CancellationToken;
            if (ct.IsCancellationRequested)
                return;

            try
            {
                foreach (string filename in filenames)
                {
                    if (eraseAndKeepFile)
                        await m_fileHandler.Erase(filename, ct).ConfigureAwait(false);
                    else
                        await m_fileHandler.Delete(filename, ct).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Save Async] SaveManager.DeleteFileOperationAsync() - Exception: {e.Message}\n{e.StackTrace}");
                throw;
            }
        }

        static string SaveablesToJson(List<IBoxedSaveable> saveables)
        {
            if (saveables == null)
                throw new ArgumentNullException(nameof(saveables));

            var array = new JArray();

            foreach (var s in saveables)
            {
                object data = null;
                try
                {
                    data = s.CaptureStateBoxed();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Save Async] SaveManager.SaveablesToJson() - Failed to capture state for ISaveable with key \"{s.Key}\": {e.Message}\n{e.StackTrace}");
                }

                var token = JToken.FromObject(data, JsonSerializer.CreateDefault(s_jsonNoTypes));

                var obj = new JObject
                {
                    ["Key"] = s.Key,
                    ["Version"] = s.Version,
                    ["Data"] = token
                };

                array.Add(obj);
            }

            return array.ToString(Formatting.Indented);
        }
    }
}
