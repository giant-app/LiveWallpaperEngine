﻿using Giantapp.LiveWallpaper.Engine.Renders;
using Giantapp.LiveWallpaper.Engine.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Threading;
using static Giantapp.LiveWallpaper.Engine.ScreenOption;

namespace Giantapp.LiveWallpaper.Engine
{
    public static class WallpaperApi
    {
        #region field

        private static readonly ConcurrentDictionary<string, string> _busyMethods = new ConcurrentDictionary<string, string>();
        private static System.Timers.Timer _timer;
        private static Dispatcher _uiDispatcher;
        private static CancellationTokenSource _ctsSetupPlayer = new CancellationTokenSource();

        #endregion

        #region property

        public static string[] Screens { get; private set; }
        public static LiveWallpaperOptions Options { get; private set; } = new LiveWallpaperOptions();

        //Dictionary<DeviceName，WallpaperModel>
        public static Dictionary<string, WallpaperModel> CurrentWalpapers { get; private set; } = new Dictionary<string, WallpaperModel>();

        public static bool Initialized { get; private set; }

        public static List<(WallpaperType Type, string DownloadUrl)> PlayerUrls = new List<(WallpaperType Type, string DownloadUrl)>()
        {
            (WallpaperType.Video,"https://github.com/giant-app/LiveWallpaperEngine/releases/download/v2.0.4/mpv.7z"),
            (WallpaperType.Web,"https://github.com/giant-app/LiveWallpaperEngine/releases/download/v2.0.4/web.7z"),
        };

        public static event EventHandler<SetupPlayerProgressChangedArgs> SetupPlayerProgressChangedEvent;

        #endregion

        #region public

        public static void Initlize(Dispatcher dispatcher)
        {
            _uiDispatcher = dispatcher;
            if (!Initialized)
            {
                RenderFactory.Renders.Add(new ExeRender());
                RenderFactory.Renders.Add(new VideoRender());
                RenderFactory.Renders.Add(new WebRender());
                RenderFactory.Renders.Add(new ImageRender());
                Screens = Screen.AllScreens.Select(m => m.DeviceName).ToArray();
            }
            Initialized = true;
        }

        internal static void UIInvoke(Action a)
        {
            _uiDispatcher.Invoke(a);
        }

        public static async Task<BaseApiResult<List<WallpaperModel>>> GetWallpapers(string dir)
        {
            try
            {
                if (!EnterBusyState(nameof(GetWallpapers)))
                    return new BaseApiResult<List<WallpaperModel>>() { Ok = false, Error = ErrorType.Busy };

                DirectoryInfo dirInfo = new DirectoryInfo(dir);
                if (!dirInfo.Exists)
                    return new BaseApiResult<List<WallpaperModel>>()
                    {
                        Ok = true
                    };

                List<WallpaperModel> result = new List<WallpaperModel>();
                //test E:\SteamLibrary\steamapps\workshop\content\431960
                //foreach (var item in Directory.EnumerateFiles(dir, "project.json", SearchOption.AllDirectories))
                var files = await Task.Run(() => dirInfo.GetFiles("project.json", SearchOption.AllDirectories).OrderByDescending(m => m.CreationTime));
                foreach (var item in files)
                {
                    using FileStream fs = File.OpenRead(item.FullName);
                    var info = await JsonSerializer.DeserializeAsync<WallpaperProjectInfo>(fs);
                    var saveDir = Path.GetDirectoryName(item.FullName);
                    result.Add(new WallpaperModel()
                    {
                        RunningData = new WallpaperRunningData()
                        {
                            Dir = saveDir,
                        },
                        Info = info,
                    });
                }

                return new BaseApiResult<List<WallpaperModel>>() { Data = result, Ok = true };
            }
            catch (Exception ex)
            {
                return new BaseApiResult<List<WallpaperModel>>() { Ok = false, Error = ErrorType.Exception, Message = ex.Message };
            }
            finally
            {
                QuitBusyState(nameof(GetWallpapers));
            }
        }

        public static Task<BaseApiResult> DeleteWallpaperPack(string absolutePath)
        {
            throw new NotImplementedException();
        }

        public static Task<BaseApiResult<WallpaperModel>> UpdateWallpaper(WallpaperModel source, WallpaperModel newWP)
        {
            throw new NotImplementedException();
        }

        public static Task<BaseApiResult<WallpaperModel>> CreateWallpaper(string path)
        {
            throw new NotImplementedException();
        }

        public static WallpaperType? GetWallpaperType(string wallpaper)
        {
            var currentRender = RenderFactory.GetRenderByExtension(Path.GetExtension(wallpaper));
            return currentRender?.SupportType;
        }

        public static async Task<BaseApiResult> ShowWallpaper(WallpaperModel wallpaper, params string[] screens)
        {
            if (!Initialized)
                return new BaseApiResult() { Ok = false, Message = "You need to initialize the SDK first", Error = ErrorType.Uninitialized };

            try
            {
                if (!EnterBusyState(nameof(ShowWallpaper)))
                    return BaseApiResult.BusyState();

                if (screens.Length == 0)
                    screens = Screens;

                IRender currentRender;
                if (wallpaper.Type == null)
                {
                    currentRender = RenderFactory.GetRenderByExtension(Path.GetExtension(wallpaper.Path));
                    if (currentRender == null)
                        return new ShowWallpaperResult()
                        {
                            Ok = false,
                            Error = ErrorType.NoRender,
                            Message = "This wallpaper type is not supported"
                        };

                    wallpaper.Type = currentRender.SupportType;
                }
                else
                    currentRender = RenderFactory.GetRender(wallpaper.Type.Value);

                if (currentRender == null)
                    if (wallpaper.Type == null)
                        throw new ArgumentException("Unsupported wallpaper type");

                foreach (var screenItem in screens)
                {
                    //当前屏幕没有壁纸
                    if (!CurrentWalpapers.ContainsKey(screenItem))
                        CurrentWalpapers.Add(screenItem, null);

                    var existWallpaper = CurrentWalpapers[screenItem];

                    //壁纸 路径相同
                    if (existWallpaper != null && existWallpaper.Path == wallpaper.Path)
                        continue;

                    //关闭之前的壁纸
                    await CloseWallpaper(screenItem);
                    var showResult = await currentRender.ShowWallpaper(wallpaper, screenItem);
                    if (!showResult.Ok)
                        return showResult;
                    CurrentWalpapers[screenItem] = wallpaper;
                }

                ApplyAudioSource();
                return new BaseApiResult() { Ok = true };
            }
            catch (Exception ex)
            {
                return BaseApiResult.ExceptionState(ex);
            }
            finally
            {
                QuitBusyState(nameof(ShowWallpaper));
            }
        }

        public static async Task<BaseApiResult> CloseWallpaper(params string[] screens)
        {
            try
            {
                if (!EnterBusyState(nameof(CloseWallpaper)))
                    return BaseApiResult.BusyState();

                foreach (var screenItem in screens)
                {
                    if (CurrentWalpapers.ContainsKey(screenItem))
                        CurrentWalpapers.Remove(screenItem);
                }
                await InnerCloseWallpaper(screens);
                return new BaseApiResult() { Ok = true };
            }
            catch (Exception ex)
            {
                return BaseApiResult.ExceptionState(ex);
            }
            finally
            {
                QuitBusyState(nameof(CloseWallpaper));
            }
        }

        public static Task<BaseApiResult> SetOptions(LiveWallpaperOptions options)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (!EnterBusyState(nameof(SetOptions)))
                        return BaseApiResult.BusyState();

                    Options = options;

                    ExplorerMonitor.ExplorerCreated -= ExplorerMonitor_ExpolrerCreated;
                    MaximizedMonitor.AppMaximized -= MaximizedMonitor_AppMaximized;

                    if (options.AutoRestartWhenExplorerCrash == true)
                        ExplorerMonitor.ExplorerCreated += ExplorerMonitor_ExpolrerCreated;

                    bool enableMaximized = options.ScreenOptions.ToList().Exists(m => m.WhenAppMaximized != ActionWhenMaximized.Play);
                    if (enableMaximized)
                        MaximizedMonitor.AppMaximized += MaximizedMonitor_AppMaximized;

                    StartTimer(options.AutoRestartWhenExplorerCrash || enableMaximized);

                    ApplyAudioSource();
                    return new BaseApiResult() { Ok = true };
                }
                catch (Exception ex)
                {
                    return BaseApiResult.ExceptionState(ex);
                }
                finally
                {
                    QuitBusyState(nameof(SetOptions));
                }
            });
        }

        public static BaseApiResult SetupPlayer(WallpaperType type, string url, Action<BaseApiResult> callback = null)
        {
            if (IsBusyState(nameof(SetupPlayer)))
                return BaseApiResult.BusyState();

            _ = InnertSetupPlayer(type, url).ContinueWith(m => callback?.Invoke(m.Result));

            return BaseApiResult.SuccessState();
        }

        private static async Task<BaseApiResult> InnertSetupPlayer(WallpaperType type, string url)
        {
            BaseApiResult result = null;
            try
            {
                _ctsSetupPlayer?.Cancel();
                _ctsSetupPlayer?.Dispose();
                _ctsSetupPlayer = new CancellationTokenSource();

                string downloadFile = await DownloadPlayer(type, url, _ctsSetupPlayer.Token);
                await UnpackPlayer(type, downloadFile, _ctsSetupPlayer.Token);

                result = BaseApiResult.SuccessState();
            }
            catch (OperationCanceledException)
            {
                result = BaseApiResult.ErrorState(ErrorType.Canceled);
            }
            catch (Exception ex)
            {
                result = BaseApiResult.ExceptionState(ex);
            }
            finally
            {
                SetupPlayerProgressChangedEvent?.Invoke(null, new SetupPlayerProgressChangedArgs()
                {
                    AllCompleted = true,
                    Path = url,
                    ProgressPercentage = 1,
                    ActionType = SetupPlayerProgressChangedArgs.Type.Completed,
                    Result = result
                });

                QuitBusyState(nameof(SetupPlayer));
            }
            return result;
        }

        public static async Task<BaseApiResult> StopSetupPlayer()
        {
            try
            {
                if (!EnterBusyState(nameof(StopSetupPlayer)))
                    return BaseApiResult.BusyState();

                _ctsSetupPlayer?.Cancel();

                while (IsBusyState(nameof(SetupPlayer)))
                {
                    await Task.Delay(1000);
                }
                return BaseApiResult.SuccessState();
            }
            catch (Exception ex)
            {
                return BaseApiResult.ExceptionState(ex);
            }
            finally
            {
                QuitBusyState(nameof(StopSetupPlayer));
            }
        }

        #endregion

        #region private
        //离开busy状态
        private static void QuitBusyState(string method)
        {
            if (!IsBusyState(method))
                return;

            _busyMethods.TryRemove(method, out _);
        }

        private static bool IsBusyState(string method)
        {
            if (_busyMethods.ContainsKey(method))
                return true;
            return false;
        }

        //进入busy状态，失败返回false
        private static bool EnterBusyState(string method)
        {
            if (IsBusyState(method))
                return false;

            var r = _busyMethods.TryAdd(method, null);
            return r;
        }
        private static void Pause(params string[] screens)
        {
            foreach (var screenItem in screens)
            {
                if (CurrentWalpapers.ContainsKey(screenItem))
                {
                    var wallpaper = CurrentWalpapers[screenItem];
                    wallpaper.RunningData.IsPaused = true;
                    var currentRender = RenderFactory.GetRenderByExtension(Path.GetExtension(wallpaper.Path));
                    currentRender.Pause(screens);
                }
            }
        }
        private static void Resume(params string[] screens)
        {
            foreach (var screenItem in screens)
            {
                if (CurrentWalpapers.ContainsKey(screenItem))
                {
                    var wallpaper = CurrentWalpapers[screenItem];
                    wallpaper.RunningData.IsPaused = false;
                    var currentRender = RenderFactory.GetRenderByExtension(Path.GetExtension(wallpaper.Path));
                    currentRender.Resume(screens);
                }
            }
        }
        private static async Task UnpackPlayer(WallpaperType type, string zipFile, CancellationToken token)
        {
            void ArchiveFile_UnzipProgressChanged(object sender, SevenZipUnzipProgressArgs e)
            {
                SetupPlayerProgressChangedEvent?.Invoke(null, new SetupPlayerProgressChangedArgs()
                {
                    ActionCompleted = false,
                    Path = zipFile,
                    ProgressPercentage = e.Progress,
                    ActionType = SetupPlayerProgressChangedArgs.Type.Unpacking
                });
            }

            if (File.Exists(zipFile))
            {
                string distFolder = null;
                switch (type)
                {
                    case WallpaperType.Web:
                        distFolder = WebRender.PlayerFolderName;
                        break;
                    case WallpaperType.Video:
                        distFolder = VideoRender.PlayerFolderName;
                        break;
                }
                SevenZip archiveFile = new SevenZip(zipFile);
                archiveFile.UnzipProgressChanged += ArchiveFile_UnzipProgressChanged;
                string dist = $@"{Options.ExternalPlayerFolder}\{distFolder}";

                try
                {
                    await Task.Run(() => archiveFile.Extract(dist, token));
                    SetupPlayerProgressChangedEvent?.Invoke(null, new SetupPlayerProgressChangedArgs()
                    {
                        ActionCompleted = true,
                        Path = zipFile,
                        ProgressPercentage = 1,
                        ActionType = SetupPlayerProgressChangedArgs.Type.Unpacking
                    });
                }
                catch (Exception)
                {
                    throw;
                }
                finally
                {
                    archiveFile.UnzipProgressChanged -= ArchiveFile_UnzipProgressChanged;
                }
            }
        }
        private static async Task<string> DownloadPlayer(WallpaperType type, string url, CancellationToken token)
        {
            string downloadFile = Path.Combine(Options.ExternalPlayerFolder, $"tmp{type}.7z");
            if (File.Exists(downloadFile) && await SevenZip.CanOpenAsync(downloadFile))
                return downloadFile;

            try
            {
                await DownloadFileAsync(url, downloadFile, token, (c, t) =>
                {
                    var args = new SetupPlayerProgressChangedArgs()
                    {
                        ActionCompleted = false,
                        Path = url,
                        ProgressPercentage = (float)c / t,
                        ActionType = SetupPlayerProgressChangedArgs.Type.Downloading
                    };
                    SetupPlayerProgressChangedEvent?.Invoke(null, args);
                });

                SetupPlayerProgressChangedEvent?.Invoke(null, new SetupPlayerProgressChangedArgs()
                {
                    ActionCompleted = true,
                    Path = url,
                    ProgressPercentage = 1,
                    ActionType = SetupPlayerProgressChangedArgs.Type.Downloading
                });

                return downloadFile;
            }
            catch (Exception)
            {
                throw;
            }
        }
        public static async Task DownloadFileAsync(string uri, string distFile, CancellationToken cancellationToken, Action<long, long> progressCallback = null)
        {
            using HttpClient client = new HttpClient();
            Debug.WriteLine($"download {uri}");
            using HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            await Task.Run(() =>
            {
                var dir = Path.GetDirectoryName(distFile);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            });

            using FileStream distFileStream = new FileStream(distFile, FileMode.OpenOrCreate, FileAccess.Write);
            if (progressCallback != null)
            {
                long length = response.Content.Headers.ContentLength ?? -1;
                await using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                byte[] buffer = new byte[4096];
                int read;
                int totalRead = 0;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await distFileStream.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                    totalRead += read;
                    progressCallback(totalRead, length);
                }
                Debug.Assert(totalRead == length || length == -1);
            }
            else
            {
                await response.Content.CopyToAsync(distFileStream).ConfigureAwait(false);
            }
        }
        private static void ApplyAudioSource()
        {
            //设置音源
            foreach (var screen in Screens)
            {
                if (CurrentWalpapers.ContainsKey(screen))
                {
                    var wallpaper = CurrentWalpapers[screen];
                    var currentRender = RenderFactory.GetRender(wallpaper);
                    currentRender.SetVolume(screen == Options.AudioScreen ? 100 : 0, screen);
                }
            }
        }

        private static async Task InnerCloseWallpaper(params string[] screens)
        {
            foreach (var m in RenderFactory.Renders)
                await m.CloseWallpaperAsync(screens);
        }

        private static void StartTimer(bool enable)
        {
            if (enable)
            {
                if (_timer == null)
                    _timer = new System.Timers.Timer(1000);

                _timer.Elapsed -= Timer_Elapsed;
                _timer.Elapsed += Timer_Elapsed;
                _timer.Start();
            }
            else
            {
                if (_timer != null)
                {
                    _timer.Elapsed -= Timer_Elapsed;
                    _timer.Stop();
                    _timer = null;
                }
            }
        }

        #endregion

        #region callback

        private static void Timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            _timer?.Stop();
            ExplorerMonitor.Check();
            MaximizedMonitor.Check();
            _timer?.Start();
        }

        private static void ExplorerMonitor_ExpolrerCreated(object sender, EventArgs e)
        {
            //重启
            Application.Restart();
        }
        private static async void MaximizedMonitor_AppMaximized(object sender, AppMaximizedEvent e)
        {
            var maximizedScreens = e.MaximizedScreens.Select((m, i) => m.DeviceName).ToList();
            bool anyScreenMaximized = maximizedScreens.Count > 0;
            foreach (var item in Options.ScreenOptions)
            {
                string currentScreen = item.Screen;
                bool currentScreenMaximized = maximizedScreens.Contains(currentScreen) || Options.AppMaximizedEffectAllScreen && anyScreenMaximized;

                switch (item.WhenAppMaximized)
                {
                    case ActionWhenMaximized.Pause:
                        if (currentScreenMaximized)
                            Pause(currentScreen);
                        else
                            Resume(currentScreen);
                        break;
                    case ActionWhenMaximized.Stop:
                        if (currentScreenMaximized)
                        {
                            await InnerCloseWallpaper(currentScreen);
                            CurrentWalpapers[currentScreen].RunningData.IsStopedTemporary = true;
                        }
                        else if (CurrentWalpapers.ContainsKey(currentScreen))
                        {
                            //_ = ShowWallpaper(CurrentWalpapers[currentScreen], currentScreen);

                            var wallpaper = CurrentWalpapers[currentScreen];
                            var currentRender = RenderFactory.GetRenderByExtension(Path.GetExtension(wallpaper.Path));
                            await currentRender.ShowWallpaper(wallpaper, currentScreen);
                        }
                        break;
                    case ActionWhenMaximized.Play:
                        CurrentWalpapers[currentScreen].RunningData.IsStopedTemporary = false;
                        break;
                }
            }
        }
        #endregion
    }
}
