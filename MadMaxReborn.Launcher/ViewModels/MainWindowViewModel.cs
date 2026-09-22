using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using MadMaxReborn.Services;
using MadMaxReborn.Models;

namespace MadMaxReborn.Launcher.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private const string GitHubClientId = "Ov23liWbRRu3zwTB3up0";

    [ObservableProperty] private string _currentStepText = "";
    [ObservableProperty] private string _stepDescription = "";
    [ObservableProperty] private string _primaryButtonText = "";
    [ObservableProperty] private string _secondaryButtonText = "Cancel";
    [ObservableProperty] private string _terminalOutput = "MadMax Reborn Tracker initialized...\n";
    [ObservableProperty] private bool _isStatusVisible;
    [ObservableProperty] private bool _isPrimaryEnabled = true;

    [ObservableProperty] private string _crewsText = "-";
    [ObservableProperty] private string _pennySavedText = "-";
    [ObservableProperty] private string _dividendText = "-";
    [ObservableProperty] private string _estimateText = "-";

    [ObservableProperty] private bool _isWaiting;
    [ObservableProperty] private double _waitProgress;
    [ObservableProperty] private string _waitRemainingText = "";

    [ObservableProperty] private bool _isGithubPopupVisible;
    [ObservableProperty] private string _githubUserCode = "";
    [ObservableProperty] private string _githubVerificationUri = "";
    [ObservableProperty] private string _githubAuthMessage = "Waiting for authorization...";

    private int _step;
    private string _selectedFilePath = "";
    private SaveAnalysisResult? _currentSave;
    private readonly SaveScannerService _scanner = new();
    
    private CancellationTokenSource? _githubPollingCts;
    private CancellationTokenSource? _waitCts;
    private string? _cachedAccessToken;

    private double _requiredWaitHours;
    private string? _cachedDeviceCode;
    private int _cachedInterval;
    private DateTime _codeExpiration;

    private static readonly byte[] PennySavedHash = [0x39, 0x86, 0x60, 0x6C];
    private static readonly byte[] DividendHash = [0x9D, 0x83, 0x01, 0xAD];

    public MainWindowViewModel()
    {
        SetStep(1);
    }

    private void Log(string message)
    {
        Dispatcher.UIThread.Post(() => TerminalOutput += $"[{DateTime.Now:HH:mm:ss}] {message}\n");
    }

    [RelayCommand]
    private async Task CopyLogAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.Clipboard != null)
        {
            await desktop.MainWindow.Clipboard.SetTextAsync(TerminalOutput);
        }
    }

    [RelayCommand]
    private void OpenReadme()
    {
        Process.Start(new ProcessStartInfo { FileName = "https://github.com/adamstark1/MadMax-Reborn-PS4", UseShellExecute = true });
    }

    private void SetStep(int step)
    {
        _step = step;
        IsStatusVisible = (_step == 2);

        switch (_step)
        {
            case 1:
                CurrentStepText = "Step 1/2: Select Save File";
                StepDescription = "Click Browse to select your decrypted GAMESAVE file.";
                PrimaryButtonText = "Browse File";
                SecondaryButtonText = "Cancel";
                _selectedFilePath = "";
                IsPrimaryEnabled = true;
                break;
            case 2:
                CurrentStepText = "Step 2/2: Unlock";
                PrimaryButtonText = "Start Unlock Process";
                SecondaryButtonText = "Back";
                break;
        }
    }

    [RelayCommand]
    private async Task ExecutePrimaryActionAsync()
    {
        try
        {
            if (_step == 1)
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
                {
                    var options = new FilePickerOpenOptions
                    {
                        Title = "Select GAMESAVE file",
                        AllowMultiple = false
                    };
                    
                    var files = await desktop.MainWindow.StorageProvider.OpenFilePickerAsync(options);
                    if (files.Count > 0)
                    {
                        string path = files[0].Path.LocalPath;
                        string fileName = Path.GetFileName(path);
                        if (!fileName.StartsWith("GAMESAVE", StringComparison.OrdinalIgnoreCase) || Path.HasExtension(path))
                        {
                            Log("[ERROR] Invalid file type! Please select a valid decrypted GAMESAVE file.");
                            return;
                        }
                        HandleFileSelected(path);
                    }
                }
            }
            else if (_step == 2)
            {
                await ProcessUnlockAsync();
            }
        }
        catch (Exception ex)
        {
            Log($"[ERROR] {ex.Message}");
        }
    }

    [RelayCommand]
    private void SkipStep()
    {
        if (IsWaiting)
        {
            Log("[INFO] Process stopped by user. Progress saved up to the last hour.");
            _waitCts?.Cancel();
            IsWaiting = false;
            IsPrimaryEnabled = true;
            SecondaryButtonText = "Cancel";
        }
        else
        {
            SetStep(1);
        }
    }

    public void HandleFileSelected(string path)
    {
        try
        {
            Log($"Analyzing file: {Path.GetFileName(path)}...");
            _selectedFilePath = path;
            RefreshSaveDataSilently();
            SetStep(2);
        }
        catch (Exception ex)
        {
            Log($"[ERROR] Failed to read save: {ex.Message}");
        }
    }

    private void RefreshSaveDataSilently()
    {
        _currentSave = _scanner.Analyze(_selectedFilePath);

        int crews = _currentSave.ScrapCrewsBuilt;
        CrewsText = crews.ToString() + " / 4 Built";

        bool pennyDone = _currentSave.PennySaved.Completed;
        int pennyCur = _currentSave.PennySaved.CurrentValue;
        int pennyTarget = _currentSave.PennySaved.TargetValue;
        PennySavedText = pennyDone ? "COMPLETED" : pennyCur.ToString() + " / " + pennyTarget.ToString();

        bool divDone = _currentSave.Dividend.Completed;
        int divCur = _currentSave.Dividend.CurrentValue;
        int divTarget = _currentSave.Dividend.TargetValue;
        DividendText = divDone ? "COMPLETED" : divCur.ToString() + " / " + divTarget.ToString();

        int totalRemaining = 2000 - divCur;
        if (pennyDone && divDone) totalRemaining = 0;

        if (totalRemaining <= 0)
        {
            EstimateText = "Challenges already completed.";
            IsPrimaryEnabled = false;
            _requiredWaitHours = 0;
        }
        else
        {
            int rate = Math.Max(1, crews * 50);
            _requiredWaitHours = (double)totalRemaining / rate;
            EstimateText = "~" + _requiredWaitHours.ToString("F1") + " hours of gameplay required";
            IsPrimaryEnabled = true;
        }
    }

    [RelayCommand]
    private async Task CopyGithubCodeAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.Clipboard != null)
        {
            await desktop.MainWindow.Clipboard.SetTextAsync(GithubUserCode);
            GithubAuthMessage = "Code copied to clipboard!";
        }
    }

    [RelayCommand]
    private void OpenGithubAuth()
    {
        if (!string.IsNullOrEmpty(GithubVerificationUri))
        {
            Process.Start(new ProcessStartInfo { FileName = GithubVerificationUri, UseShellExecute = true });
        }
    }

    [RelayCommand]
    private void SkipGithubAuth()
    {
        _githubPollingCts?.Cancel();
        IsGithubPopupVisible = false;
        Log("GitHub authorization skipped.");
    }

    private async Task ProcessUnlockAsync()
    {
        if (_currentSave == null) return;
        IsPrimaryEnabled = false;

        Log("Initiating GitHub Device Flow authorization...");
        bool isStarred = await AuthenticateAndCheckStarAsync();

        if (isStarred)
        {
            Log("[SUCCESS] Star ⭐ verified! Patching save file immediately...");
            PatchSaveFileCompleted();
        }
        else
        {
            Log("[WARNING] Star missing or bypassed. Starting manual unlock...");
            await ApplyDelayAndPatchAsync();
        }
    }

    private async Task ApplyDelayAndPatchAsync()
    {
        IsWaiting = true;
        SecondaryButtonText = "Stop";
        _waitCts = new CancellationTokenSource();

        int rate = Math.Max(1, _currentSave!.ScrapCrewsBuilt * 50);
        TimeSpan totalWait = TimeSpan.FromHours(_requiredWaitHours);
        DateTime startTime = DateTime.UtcNow;
        DateTime endTime = startTime.Add(totalWait);
        
        int hoursPassed = 0;

        try
        {
            while (DateTime.UtcNow < endTime)
            {
                _waitCts.Token.ThrowIfCancellationRequested();

                TimeSpan elapsed = DateTime.UtcNow - startTime;
                TimeSpan remaining = endTime - DateTime.UtcNow;
                
                WaitRemainingText = $"Time remaining: {(int)remaining.TotalHours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                WaitProgress = 100.0 - (remaining.TotalSeconds / totalWait.TotalSeconds * 100.0);
                
                if (elapsed.TotalHours >= (hoursPassed + 1))
                {
                    hoursPassed++;
                    IncrementSaveProgress(rate);
                    Log($"[INFO] 1 hour elapsed. Save file updated (+{rate} Scrap).");
                }

                await Task.Delay(1000, _waitCts.Token);
            }

            IsWaiting = false;
            SecondaryButtonText = "Cancel";
            PatchSaveFileCompleted();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void IncrementSaveProgress(int amount)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(_selectedFilePath);

            void AddAmount(byte[] hash, int maxVal)
            {
                int pos = FindPattern(bytes, hash);
                if (pos != -1 && pos + 16 <= bytes.Length)
                {
                    int current = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 4, 4));
                    current += amount;
                    if (current >= maxVal)
                    {
                        current = maxVal;
                        bytes[pos - 4] = 0x01;
                        bytes[pos - 3] = 0x00;
                        bytes[pos - 2] = 0x00;
                        bytes[pos - 1] = 0x00;
                    }
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(pos + 4, 4), current);
                }
            }

            AddAmount(PennySavedHash, 500);
            AddAmount(DividendHash, 2000);

            File.WriteAllBytes(_selectedFilePath, bytes);
            Dispatcher.UIThread.Post(RefreshSaveDataSilently);
        }
        catch (Exception ex)
        {
            Log($"[ERROR] Incremental update failed: {ex.Message}");
        }
    }

    private async Task<bool> AuthenticateAndCheckStarAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Accept", "application/json");

            if (!string.IsNullOrEmpty(_cachedAccessToken))
            {
                using var authClient = new HttpClient();
                authClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_cachedAccessToken}");
                authClient.DefaultRequestHeaders.Add("User-Agent", "MadMaxReborn-App");

                var starResp = await authClient.GetAsync("https://api.github.com/user/starred/adamstark1/MadMax-Reborn-PS4");
                
                if (starResp.StatusCode == System.Net.HttpStatusCode.NoContent)
                {
                    return true;
                }
                if (starResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _cachedAccessToken = null;
                }
                else
                {
                    return false;
                }
            }

            if (string.IsNullOrEmpty(_cachedDeviceCode) || DateTime.UtcNow > _codeExpiration)
            {
                var requestData = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("client_id", GitHubClientId) });
                var response = await client.PostAsync("https://github.com/login/device/code", requestData);
                if (!response.IsSuccessStatusCode) return false;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                _cachedDeviceCode = doc.RootElement.GetProperty("device_code").GetString()!;
                GithubUserCode = doc.RootElement.GetProperty("user_code").GetString()!;
                GithubVerificationUri = doc.RootElement.GetProperty("verification_uri").GetString()!;
                _cachedInterval = doc.RootElement.GetProperty("interval").GetInt32();
                
                int expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
                _codeExpiration = DateTime.UtcNow.AddSeconds(expiresIn - 60);
            }

            GithubAuthMessage = "Enter code in the opened browser window...";
            IsGithubPopupVisible = true;

            _githubPollingCts = new CancellationTokenSource();
            string? accessToken = null;

            while (!_githubPollingCts.IsCancellationRequested && DateTime.UtcNow < _codeExpiration)
            {
                await Task.Delay(_cachedInterval * 1000, _githubPollingCts.Token);

                var pollData = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", GitHubClientId),
                    new KeyValuePair<string, string>("device_code", _cachedDeviceCode),
                    new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
                });

                var pollResp = await client.PostAsync("https://github.com/login/oauth/access_token", pollData);
                var pollJson = await pollResp.Content.ReadAsStringAsync();

                if (pollJson.Contains("access_token"))
                {
                    using var pollDoc = JsonDocument.Parse(pollJson);
                    accessToken = pollDoc.RootElement.GetProperty("access_token").GetString();
                    break;
                }
                if (pollJson.Contains("authorization_pending")) continue;
                if (pollJson.Contains("slow_down"))
                {
                    _cachedInterval += 5;
                    continue;
                }
                
                _cachedDeviceCode = null;
                break;
            }

            IsGithubPopupVisible = false;

            if (string.IsNullOrEmpty(accessToken)) return false;

            _cachedAccessToken = accessToken;

            using var authClient2 = new HttpClient();
            authClient2.DefaultRequestHeaders.Add("Authorization", $"Bearer {_cachedAccessToken}");
            authClient2.DefaultRequestHeaders.Add("User-Agent", "MadMaxReborn-App");

            var starResp2 = await authClient2.GetAsync("https://api.github.com/user/starred/adamstark1/MadMax-Reborn-PS4");
            return starResp2.StatusCode == System.Net.HttpStatusCode.NoContent;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log($"[AUTH ERROR] {ex.Message}");
            IsGithubPopupVisible = false;
            return false;
        }
    }

    private void PatchSaveFileCompleted()
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(_selectedFilePath);

            int posPenny = FindPattern(bytes, PennySavedHash);
            if (posPenny != -1 && posPenny + 16 <= bytes.Length)
            {
                bytes[posPenny - 4] = 0x01;
                bytes[posPenny - 3] = 0x00;
                bytes[posPenny - 2] = 0x00;
                bytes[posPenny - 1] = 0x00;
                bytes[posPenny + 4] = 0xF4;
                bytes[posPenny + 5] = 0x01;
                bytes[posPenny + 6] = 0x00;
                bytes[posPenny + 7] = 0x00;
                bytes[posPenny + 8] = 0x2D;
                bytes[posPenny + 9] = 0x81;
                bytes[posPenny + 10] = 0x54;
                bytes[posPenny + 11] = 0xEA;
            }

            int posDiv = FindPattern(bytes, DividendHash);
            if (posDiv != -1 && posDiv + 16 <= bytes.Length)
            {
                bytes[posDiv - 4] = 0x01;
                bytes[posDiv - 3] = 0x00;
                bytes[posDiv - 2] = 0x00;
                bytes[posDiv - 1] = 0x00;
                bytes[posDiv + 4] = 0xD0;
                bytes[posDiv + 5] = 0x07;
                bytes[posDiv + 6] = 0x00;
                bytes[posDiv + 7] = 0x00;
                bytes[posDiv + 8] = 0x2D;
                bytes[posDiv + 9] = 0x81;
                bytes[posDiv + 10] = 0x54;
                bytes[posDiv + 11] = 0xEA;
            }

            File.WriteAllBytes(_selectedFilePath, bytes);
            Log("[SUCCESS] Save file successfully modified! Challenges are now COMPLETED.");
            Dispatcher.UIThread.Post(RefreshSaveDataSilently);
        }
        catch (Exception ex)
        {
            Log($"[PATCH ERROR] {ex.Message}");
        }
        finally
        {
            IsPrimaryEnabled = true;
        }
    }

    private static int FindPattern(byte[] src, byte[] pattern)
    {
        int limit = src.Length - pattern.Length + 1;
        for (int i = 0; i < limit; i++)
        {
            if (src[i] != pattern[0]) continue;
            bool match = true;
            for (int j = 1; j < pattern.Length; j++)
            {
                if (src[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }
}