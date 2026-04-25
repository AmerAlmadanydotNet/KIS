using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using KesFile.Models;
using KesFile.Services;

namespace KesFile.ViewModels
{
    /// <summary>ViewModel for the Create Archive page.</summary>
    public class CreateArchiveViewModel : ViewModelBase
    {
        private readonly KesArchiveService _service = new();
        private CoreDispatcher? _dispatcher;

        // ─── File/Folder list ─────────────────────────────────────────────
        public ObservableCollection<IStorageItem> FilesToCompress { get; } = new();

        // ─── Options ─────────────────────────────────────────────────────────
        private int _compressionAlgorithmIndex = 1; // 0=None 1=LZMA 2=Deflate
        public int CompressionAlgorithmIndex
        {
            get => _compressionAlgorithmIndex;
            set => SetProperty(ref _compressionAlgorithmIndex, value);
        }

        private int _compressionSpeedIndex = 2; // 0=Fastest 1=Fast 2=Normal 3=Maximum
        public int CompressionSpeedIndex
        {
            get => _compressionSpeedIndex;
            set => SetProperty(ref _compressionSpeedIndex, value);
        }

        private bool _enableEncryption;
        public bool EnableEncryption
        {
            get => _enableEncryption;
            set => SetProperty(ref _enableEncryption, value);
        }

        private string _password = string.Empty;
        public string Password
        {
            get => _password;
            set => SetProperty(ref _password, value);
        }

        private string _passwordHint = string.Empty;
        public string PasswordHint
        {
            get => _passwordHint;
            set => SetProperty(ref _passwordHint, value);
        }

        private bool _encryptFileNames;
        public bool EncryptFileNames
        {
            get => _encryptFileNames;
            set => SetProperty(ref _encryptFileNames, value);
        }

        private bool _enableSplit;
        public bool EnableSplit
        {
            get => _enableSplit;
            set => SetProperty(ref _enableSplit, value);
        }

        private string _splitSizeMb = "100";
        public string SplitSizeMb
        {
            get => _splitSizeMb;
            set => SetProperty(ref _splitSizeMb, value);
        }

        private bool _hasChecksums = true;
        public bool HasChecksums
        {
            get => _hasChecksums;
            set => SetProperty(ref _hasChecksums, value);
        }

        // ─── Progress ────────────────────────────────────────────────────────
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            set => SetProperty(ref _progressValue, value);
        }

        private string _progressText = string.Empty;
        public string ProgressText
        {
            get => _progressText;
            set => SetProperty(ref _progressText, value);
        }

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        // ─── Commands / Actions ──────────────────────────────────────────────

        public async Task AddFilesAsync()
        {
            var picker = new FileOpenPicker
            {
                ViewMode           = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            picker.FileTypeFilter.Add("*");

            IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
            foreach (var f in files)
                if (!FilesToCompress.Contains(f))
                    FilesToCompress.Add(f);
        }

        public async Task AddFolderAsync()
        {
            var picker = new FolderPicker
            {
                ViewMode               = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            picker.FileTypeFilter.Add("*");

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder != null && !FilesToCompress.Contains(folder))
                FilesToCompress.Add(folder);
        }

        public void RemoveItem(IStorageItem item) => FilesToCompress.Remove(item);

        public void ClearFiles() => FilesToCompress.Clear();

        public void AddDroppedFiles(IReadOnlyList<IStorageItem> items)
        {
            foreach (var item in items)
                if (!FilesToCompress.Contains(item))
                    FilesToCompress.Add(item);
        }

        public async Task<bool> CreateArchiveAsync()
        {
            if (FilesToCompress.Count == 0)
            {
                StatusMessage = "Please add at least one file or folder.";
                return false;
            }
            if (EnableEncryption && string.IsNullOrWhiteSpace(Password))
            {
                StatusMessage = "Please enter a password for encryption.";
                return false;
            }

            // Default archive name comes from the first added item.
            string suggestedName = "Archive";
            var firstItem = FilesToCompress[0];
            if (firstItem is IStorageItem storageItem && !string.IsNullOrEmpty(storageItem.Name))
            {
                suggestedName = storageItem is StorageFile sf
                    ? System.IO.Path.GetFileNameWithoutExtension(sf.Name)
                    : storageItem.Name;
                if (string.IsNullOrWhiteSpace(suggestedName))
                    suggestedName = "Archive";
            }

            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop,
                SuggestedFileName      = suggestedName
            };
            savePicker.FileTypeChoices.Add("KIS Archive", new List<string> { ".kis" });

            StorageFile? destFile = await savePicker.PickSaveFileAsync();
            if (destFile == null) return false;

            // Flatten files+folders into (file, relativePath) pairs
            var flatList = new List<(StorageFile File, string RelativePath)>();
            foreach (var item in FilesToCompress)
                await FlattenItemAsync(item, string.Empty, flatList);

            if (flatList.Count == 0)
            {
                StatusMessage = "No files found to compress (folders may be empty).";
                return false;
            }

            var options = BuildOptions();
            IsBusy        = true;
            ProgressValue = 0;
            ProgressText  = "Scanning files...";
            StatusMessage = string.Empty;

            // Capture UI dispatcher so progress callbacks can marshal back to UI thread
            _dispatcher = Windows.UI.Core.CoreWindow.GetForCurrentThread()?.Dispatcher
                       ?? Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher;

            var sw  = Stopwatch.StartNew();
            var cts = new CancellationTokenSource();

            _service.Progress += (_, e) =>
            {
                // Must dispatch to UI thread — progress fires on a background thread
                _ = _dispatcher?.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    double pct = e.Fraction * 100.0;
                    ProgressValue = pct;

                    // Build ETA string
                    string eta = "";
                    if (e.Fraction > 0.01 && e.Fraction < 1.0)
                    {
                        double elapsedSec  = sw.Elapsed.TotalSeconds;
                        double totalSec    = elapsedSec / e.Fraction;
                        double remainSec   = totalSec - elapsedSec;
                        eta = "  –  " + FormatTime(remainSec) + " remaining";
                    }

                    // Size progress
                    string sizePart = e.TotalBytes > 0
                        ? $"  ({FormatBytes(e.ProcessedBytes)} / {FormatBytes(e.TotalBytes)})"
                        : "";

                    string fileName = System.IO.Path.GetFileName(e.CurrentFile);
                    ProgressText = $"{(int)pct}%  [{e.Processed}/{e.Total}]{sizePart}{eta}"
                                 + (fileName.Length > 0 ? $"\n{fileName}" : "");
                });
            };

            try
            {
                await _service.CreateArchiveAsync(flatList, destFile, options, cts.Token);
                ProgressValue = 100;
                ProgressText  = "Done!";
                StatusMessage = $"Archive created ({flatList.Count} file(s)): {destFile.Name}";
                return true;
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Operation cancelled.";
                return false;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>Recursively enumerates a file or folder into (file, relativePath) pairs.</summary>
        private static async Task FlattenItemAsync(
            IStorageItem item, string prefix,
            List<(StorageFile, string)> output)
        {
            if (item is StorageFile file)
            {
                string path = string.IsNullOrEmpty(prefix) ? file.Name : prefix + "/" + file.Name;
                output.Add((file, path));
            }
            else if (item is StorageFolder folder)
            {
                string folderPrefix = string.IsNullOrEmpty(prefix)
                    ? folder.Name
                    : prefix + "/" + folder.Name;

                IReadOnlyList<IStorageItem> children = await folder.GetItemsAsync();
                foreach (var child in children)
                    await FlattenItemAsync(child, folderPrefix, output);
            }
        }

        private static string FormatTime(double seconds)
        {
            if (seconds < 60)  return $"{(int)seconds}s";
            if (seconds < 3600) return $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s";
            return $"{(int)(seconds / 3600)}h {(int)((seconds % 3600) / 60)}m";
        }

        private static string FormatBytes(ulong bytes)
        {
            if (bytes < 1024UL)              return $"{bytes} B";
            if (bytes < 1024UL * 1024)       return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024UL * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        private ArchiveOptions BuildOptions()
        {
            KesCompressionType ct = CompressionAlgorithmIndex switch
            {
                0 => KesCompressionType.None,
                1 => KesCompressionType.Lzma,
                2 => KesCompressionType.Deflate,
                _ => KesCompressionType.Lzma
            };

            CompressionSpeed speed = (CompressionSpeed)CompressionSpeedIndex;

            ulong splitBytes = ulong.TryParse(SplitSizeMb, out ulong mb)
                ? mb * 1024 * 1024
                : 100 * 1024 * 1024;

            return new ArchiveOptions
            {
                CompressionType  = ct,
                CompressionSpeed = speed,
                EnableEncryption = EnableEncryption,
                Password         = Password,
                PasswordHint     = PasswordHint,
                EncryptFileNames = EncryptFileNames,
                EnableSplit      = EnableSplit,
                SplitSizeBytes   = splitBytes,
                HasChecksums     = HasChecksums,
                PreserveMetadata = true
            };
        }
    }
}
