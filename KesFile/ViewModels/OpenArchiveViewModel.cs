using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using KesFile.Models;
using KesFile.Services;

namespace KesFile.ViewModels
{
    /// <summary>ViewModel for a single entry displayed in the archive browser.</summary>
    public class ArchiveEntryViewModel : ViewModelBase
    {
        public KesEntryInfo Entry { get; }

        public string Name             => Entry.FileName;
        public string Path             => Entry.Path;
        public string OriginalSize     => Entry.OriginalSizeText;
        public string StoredSize       => Entry.StoredSizeText;
        public string CompressionRatio => Entry.CompressionRatioText;
        public string Modified         => Entry.ModifiedUtc.ToLocalTime().ToString("g");
        public string TypeIcon         => Entry.EntryType == KesEntryType.Directory ? "\uED25" : "\uE8A5"; // folder / file glyph

        public ArchiveEntryViewModel(KesEntryInfo entry) => Entry = entry;
    }

    /// <summary>ViewModel for the Open / Browse Archive page.</summary>
    public class OpenArchiveViewModel : ViewModelBase
    {
        private readonly KesArchiveService _service = new();

        // ─── Archive info ─────────────────────────────────────────────────────
        private KesHeader?    _header;
        private StorageFile?  _archiveFile;

        public ObservableCollection<ArchiveEntryViewModel> Entries { get; } = new();

        private string _archiveName = string.Empty;
        public string ArchiveName
        {
            get => _archiveName;
            set => SetProperty(ref _archiveName, value);
        }

        private string _archiveInfo = string.Empty;
        public string ArchiveInfo
        {
            get => _archiveInfo;
            set => SetProperty(ref _archiveInfo, value);
        }

        private bool _isEncrypted;
        public bool IsEncrypted
        {
            get => _isEncrypted;
            set => SetProperty(ref _isEncrypted, value);
        }

        private string _passwordHint = string.Empty;
        public string PasswordHint
        {
            get => _passwordHint;
            set => SetProperty(ref _passwordHint, value);
        }

        private string _password = string.Empty;
        public string Password
        {
            get => _password;
            set => SetProperty(ref _password, value);
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

        // ─── Open ─────────────────────────────────────────────────────────────

        public async Task<bool> BrowseAndOpenArchiveAsync()
        {
            var picker = new FileOpenPicker
            {
                ViewMode               = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            picker.FileTypeFilter.Add(".kis");
            picker.FileTypeFilter.Add(".kes");
            picker.FileTypeFilter.Add("*");

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file == null) return false;

            return await OpenArchiveAsync(file);
        }

        public async Task<bool> OpenArchiveAsync(StorageFile file)
        {
            _archiveFile = file;
            ArchiveName  = file.Name;
            Entries.Clear();
            StatusMessage = string.Empty;
            IsEncrypted   = false;
            PasswordHint  = string.Empty;
            Password      = string.Empty;

            try
            {
                // Peek first — this never throws for encrypted archives.
                var (peekHeader, hint) = await _service.PeekArchiveAsync(file);
                _header      = peekHeader;
                IsEncrypted  = peekHeader.IsEncrypted;
                PasswordHint = hint;

                if (!IsEncrypted)
                {
                    var (hdr, entries) = await _service.OpenArchiveAsync(file, null);
                    _header = hdr;
                    PopulateEntries(hdr, entries);
                }
                else
                {
                    StatusMessage = "Archive is encrypted. Enter the password and click Unlock.";
                }

                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error opening archive: {ex.Message}";
                return false;
            }
        }

        public async Task<bool> UnlockArchiveAsync()
        {
            if (_archiveFile == null) return false;
            try
            {
                var (hdr, entries) = await _service.OpenArchiveAsync(_archiveFile, Password);
                _header = hdr;
                PopulateEntries(hdr, entries);
                StatusMessage = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Wrong password or corrupted archive: {ex.Message}";
                return false;
            }
        }

        // ─── Extract ──────────────────────────────────────────────────────────

        public async Task<bool> ExtractAllAsync()
        {
            if (_archiveFile == null || _header == null) return false;

            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop,
                ViewMode               = PickerViewMode.List
            };
            picker.FileTypeFilter.Add("*");
            StorageFolder? dest = await picker.PickSingleFolderAsync();
            if (dest == null) return false;

            return await ExtractToAsync(dest);
        }

        public async Task<bool> ExtractSelectedAsync(IList<ArchiveEntryViewModel> selected)
        {
            if (_archiveFile == null || _header == null) return false;

            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop,
                ViewMode               = PickerViewMode.List
            };
            picker.FileTypeFilter.Add("*");
            StorageFolder? dest = await picker.PickSingleFolderAsync();
            if (dest == null) return false;

            IsBusy        = true;
            ProgressValue = 0;
            StatusMessage = string.Empty;

            try
            {
                int total = selected.Count;
                for (int i = 0; i < total; i++)
                {
                    ProgressText  = $"Extracting: {selected[i].Name}  ({i + 1}/{total})";
                    ProgressValue = (double)(i + 1) / total * 100;
                    await _service.ExtractEntryAsync(_archiveFile, selected[i].Entry, dest, _header, Password);
                }
                StatusMessage = $"Extracted {total} file(s) to {dest.Path}";
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Extraction error: {ex.Message}";
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task<bool> ExtractToAsync(StorageFolder dest)
        {
            IsBusy        = true;
            ProgressValue = 0;
            StatusMessage = string.Empty;

            _service.Progress += (_, e) =>
            {
                ProgressValue = e.Fraction * 100;
                ProgressText  = $"Extracting: {e.CurrentFile}  ({e.Processed}/{e.Total})";
            };

            try
            {
                await _service.ExtractArchiveAsync(
                    _archiveFile!, dest,
                    IsEncrypted ? Password : null,
                    CancellationToken.None);

                StatusMessage = $"Extracted to: {dest.Path}";
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Extraction error: {ex.Message}";
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ─── Helpers ─────────────────────────────────────────────────────────

        private void PopulateEntries(KesHeader hdr, IList<KesEntryInfo> entries)
        {
            Entries.Clear();
            foreach (var e in entries)
                Entries.Add(new ArchiveEntryViewModel(e));

            string comprAlgo = hdr.CompressionType switch
            {
                KesCompressionType.Lzma    => "LZMA",
                KesCompressionType.Deflate => "Deflate",
                _                          => "None"
            };

            string encInfo = hdr.IsEncrypted ? "AES-256-CBC + HMAC-SHA256" : "None";

            ArchiveInfo =
                $"Format: KesFile v{hdr.VersionMajor}.{hdr.VersionMinor}  |  " +
                $"Files: {hdr.EntryCount}  |  " +
                $"Original: {FormatSize(hdr.OriginalTotalSize)}  |  " +
                $"Compressed: {FormatSize(hdr.CompressedTotalSize)}  |  " +
                $"Algorithm: {comprAlgo}  |  " +
                $"Encryption: {encInfo}";
        }

        private static string FormatSize(ulong bytes)
        {
            if (bytes < 1024)               return $"{bytes} B";
            if (bytes < 1024 * 1024)        return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024)return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
