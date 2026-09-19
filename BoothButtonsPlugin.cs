using System;
using System.Collections.Generic;
using System.Text;
using Ink_Canvas.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace VideoShowBoothButtons
{
    /// <summary>
    /// 视频展台硬件按钮适配插件。
    /// <para>
    /// 视频展台硬件把 5 个物理按键模拟为全局热键（修饰键固定 Ctrl+Alt+Shift）：
    ///   拍照=M(0x4D)、放大=O(0x4F)、缩小=I(0x49)、旋转=U(0x55)、开关=P(0x50)。
    /// 原厂软件 VideoShow.exe 接收 M/O/I/U，HotKeyApp.exe 接收 P。
    /// 本插件在宿主内注册同一组热键，转发到内置视频展台（IVideoBoothService），
    /// 因此禁用原厂软件自启后硬件按钮依然可用。
    /// </para>
    /// </summary>
    public sealed class BoothButtonsPlugin : PluginBase
    {
        // 修饰键：Ctrl=2, Alt=1, Shift=4（IHotkeyService 约定的编码，与 RegisterHotKey 一致）
        private const uint HardwareModifiers = 2u /*Ctrl*/ | 1u /*Alt*/ | 4u /*Shift*/;

        private static readonly (string Id, uint Key, string Label)[] HotkeyDefs =
        {
            ("boothbuttons.capture", 0x4D /*M*/, "拍照"),
            ("boothbuttons.zoomin",  0x4F /*O*/, "放大"),
            ("boothbuttons.zoomout", 0x49 /*I*/, "缩小"),
            ("boothbuttons.rotate",  0x55 /*U*/, "旋转"),
            ("boothbuttons.toggle",  0x50 /*P*/, "开关"),
        };

        private IVideoBoothService _booth;
        private IHotkeyService _hotkeys;
        private INotificationService _notifications;
        private List<HotkeyBinding> _bindings = new List<HotkeyBinding>();
        private BoothButtonsSettingsView _settingsView;

        /// <summary>默认绑定：Ctrl+Alt+Shift + M/O/I/U/P。</summary>
        internal static List<HotkeyBinding> CreateDefaultBindings()
        {
            var list = new List<HotkeyBinding>();
            foreach (var def in HotkeyDefs)
            {
                list.Add(new HotkeyBinding
                {
                    Id = def.Id,
                    Label = def.Label,
                    Modifiers = HardwareModifiers,
                    Key = def.Key,
                });
            }
            return list;
        }

        /// <summary>当前生效的绑定（设置页读取用）。</summary>
        internal IReadOnlyList<HotkeyBinding> CurrentBindings
        {
            get
            {
                var copy = new List<HotkeyBinding>();
                foreach (var b in _bindings)
                {
                    copy.Add(new HotkeyBinding { Id = b.Id, Label = b.Label, Modifiers = b.Modifiers, Key = b.Key });
                }
                return copy;
            }
        }

        /// <summary>
        /// 拍照键在照片预览页的状态机：
        /// 第 1 次按：弹出「目前正处于照片预览模式，再次点击返回摄像头画面」提示（置 true）；
        /// 第 2 次按：返回直播（摄像头）画面（置 false）；
        /// 第 3 次按：正常拍照。离开照片预览页时自动复位，不影响直播页直接拍照。
        /// </summary>
        private bool _photoPreviewHintShown;

        public override void Initialize(IPluginHost host, IServiceCollection services)
        {
            base.Initialize(host, services);

            _booth = GetService<IVideoBoothService>();
            if (_booth == null)
            {
                LogError("未获取到 IVideoBoothService：宿主 API 版本需 ≥ 1.12.0，插件无法工作");
                return;
            }

            _hotkeys = GetService<IHotkeyService>();
            if (_hotkeys == null)
            {
                LogError("未获取到 IHotkeyService，无法注册硬件按钮热键");
                return;
            }

            _notifications = GetService<INotificationService>();

            LoadBindings();

            var failed = new List<string>();
            foreach (var binding in _bindings)
            {
                var action = CreateAction(binding.Id);
                if (!_hotkeys.Register(binding.Id, binding.Modifiers, binding.Key, action))
                {
                    failed.Add(DescribeBinding(binding));
                }
            }

            if (failed.Count > 0)
            {
                var message = new StringBuilder()
                    .Append("以下硬件按钮热键注册失败：")
                    .Append(string.Join("、", failed))
                    .Append("。常见原因：原厂软件（VideoShow.exe / HotKeyApp.exe）仍在运行并占用热键，")
                    .Append("请结束对应进程或禁用其自启后重启本软件。");
                LogError(message.ToString());
                _notifications?.Show(
                    "视频展台硬件按钮", message.ToString(), NotificationLevel.Warning);
            }
            else
            {
                Log("已注册 5 个视频展台硬件按钮热键：" +
                    string.Join("、", _bindings.ConvertAll(b => DescribeBinding(b))));
            }
        }

        /// <summary>
        /// 应用新的一组绑定：先全量注销再重注册；任何一项注册失败则整体回滚到旧绑定。
        /// 成功时写入插件配置目录下的 settings.json。
        /// </summary>
        internal bool ApplyBindings(IReadOnlyList<HotkeyBinding> newBindings, out string message)
        {
            message = null;
            if (newBindings == null || newBindings.Count != HotkeyDefs.Length)
            {
                message = "绑定数量不正确，未保存。";
                return false;
            }

            var pending = new List<HotkeyBinding>();
            foreach (var b in newBindings)
            {
                if (string.IsNullOrEmpty(b.Id) || b.Modifiers == 0 || b.Key == 0)
                {
                    message = $"「{b.Label}」配置无效（缺少修饰键或按键），未保存。";
                    return false;
                }
                pending.Add(new HotkeyBinding { Id = b.Id, Label = b.Label, Modifiers = b.Modifiers, Key = b.Key });
            }

            var old = CurrentBindings;
            var failed = new List<string>();
            foreach (var b in pending)
            {
                _hotkeys.Unregister(b.Id);
                if (!_hotkeys.Register(b.Id, b.Modifiers, b.Key, CreateAction(b.Id)))
                {
                    failed.Add(DescribeBinding(b));
                }
            }

            if (failed.Count > 0)
            {
                // 整体回滚到修改前的绑定（尽力而为）
                foreach (var ob in old)
                {
                    _hotkeys.Unregister(ob.Id);
                    _hotkeys.Register(ob.Id, ob.Modifiers, ob.Key, CreateAction(ob.Id));
                }
                message = "以下热键注册失败（可能被原厂软件等占用）：" + string.Join("、", failed) + "。已回滚到原设置。";
                _notifications?.Show("视频展台硬件按钮", message, NotificationLevel.Warning);
                return false;
            }

            _bindings = pending;
            try
            {
                SaveBindings();
            }
            catch (Exception ex)
            {
                LogError("保存热键配置失败: " + ex.Message);
            }
            return true;
        }

        /// <summary>从插件配置目录加载保存的绑定；文件缺失或损坏时回退默认值。</summary>
        private void LoadBindings()
        {
            _bindings = CreateDefaultBindings();
            try
            {
                var path = GetSettingsPath();
                if (!System.IO.File.Exists(path)) return;

                var json = System.IO.File.ReadAllText(path, Encoding.UTF8);
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    if (!doc.RootElement.TryGetProperty("hotkeys", out var array) ||
                        array.ValueKind != System.Text.Json.JsonValueKind.Array) return;

                    foreach (var el in array.EnumerateArray())
                    {
                        if (!el.TryGetProperty("id", out var idProp) ||
                            !el.TryGetProperty("modifiers", out var modsProp) ||
                            !el.TryGetProperty("key", out var keyProp)) continue;

                        var id = idProp.GetString();
                        if (string.IsNullOrEmpty(id)) continue;

                        uint modifiers = 0, key = 0;
                        try { modifiers = modsProp.GetUInt32(); key = keyProp.GetUInt32(); }
                        catch { continue; }

                        if (modifiers == 0 || key == 0 || key > 255) continue;

                        var target = _bindings.Find(b => b.Id == id);
                        if (target != null)
                        {
                            target.Modifiers = modifiers;
                            target.Key = key;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("读取热键配置失败，使用默认值: " + ex.Message);
            }
        }

        private void SaveBindings()
        {
            var path = GetSettingsPath();
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            };
            var json = System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    hotkeys = _bindings.ConvertAll(b => new { b.Id, b.Modifiers, b.Key }),
                },
                options);
            System.IO.File.WriteAllText(path, json, Encoding.UTF8);
        }

        private string GetSettingsPath()
        {
            var dir = !string.IsNullOrEmpty(PluginConfigFolder) ? PluginConfigFolder : PluginFolder;
            if (string.IsNullOrEmpty(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;
            return System.IO.Path.Combine(dir, "settings.json");
        }

        private string DescribeBinding(HotkeyBinding binding)
        {
            var mods = new StringBuilder();
            if ((binding.Modifiers & 2) != 0) mods.Append("Ctrl+");
            if ((binding.Modifiers & 1) != 0) mods.Append("Alt+");
            if ((binding.Modifiers & 4) != 0) mods.Append("Shift+");

            string keyText;
            try
            {
                var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)binding.Key);
                keyText = key.ToString();
            }
            catch
            {
                keyText = binding.Key.ToString("X2");
            }
            return $"{binding.Label}({mods}{keyText})";
        }

        public override void Shutdown()
        {
            if (_hotkeys != null)
            {
                foreach (var def in HotkeyDefs)
                {
                    _hotkeys.Unregister(def.Id);
                }
                _hotkeys = null;
            }

            base.Shutdown();
        }

        /// <summary>自定义设置页：5 个热键的修饰键与按键可自由修改，保存后立即重新注册。</summary>
        public override object GetSettingsView()
        {
            if (_settingsView == null)
            {
                _settingsView = new BoothButtonsSettingsView(this);
            }
            return _settingsView;
        }

        private System.Action CreateAction(string id)
        {
            // 回调可能在任意线程触发；IVideoBoothService 内部自行调度到 UI 线程
            return id switch
            {
                "boothbuttons.capture" => () => CaptureWithPhotoPreviewGuard(),
                "boothbuttons.zoomin"  => () => _booth.ZoomIn(),
                "boothbuttons.zoomout" => () => _booth.ZoomOut(),
                "boothbuttons.rotate"  => () => _booth.Rotate90(),
                "boothbuttons.toggle"  => () => _booth.Toggle(),
                _ => () => { }
            };
        }

        /// <summary>
        /// 拍照键逻辑：
        /// - 直播页（或展台未激活）：直接拍照；
        /// - 照片预览页：第 1 次按弹出提示「目前正处于照片预览模式 / 再次点击返回摄像头画面」，
        ///   第 2 次按返回直播画面，第 3 次按拍照（与用户预期一致，提示逻辑由插件负责）。
        /// </summary>
        private void CaptureWithPhotoPreviewGuard()
        {
            if (_booth.IsPhotoPreviewActive)
            {
                if (!_photoPreviewHintShown)
                {
                    _photoPreviewHintShown = true;
                    _notifications?.Show(
                        "视频展台硬件按钮",
                        "目前正处于照片预览模式\n再次点击返回摄像头画面",
                        NotificationLevel.Info);
                }
                else
                {
                    _photoPreviewHintShown = false;
                    _booth.SwitchToLiveView();
                }
                return;
            }

            // 不在照片预览页：复位状态，直接拍照
            _photoPreviewHintShown = false;
            _booth.CapturePhoto();
        }
    }
}
