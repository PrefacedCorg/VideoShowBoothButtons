namespace VideoShowBoothButtons
{
    /// <summary>
    /// 单个硬件按钮热键绑定：功能标识 + 修饰键掩码 + Win32 虚拟键码。
    /// </summary>
    public sealed class HotkeyBinding
    {
        /// <summary>功能标识（与 HotkeyDefs 中的 Id 一致）。</summary>
        public string Id { get; set; }

        /// <summary>显示名称。</summary>
        public string Label { get; set; }

        /// <summary>修饰键位掩码：Ctrl=2, Alt=1, Shift=4（与 IHotkeyService / RegisterHotKey 编码一致）。</summary>
        public uint Modifiers { get; set; }

        /// <summary>Win32 虚拟键码（如 M=0x4D）。</summary>
        public uint Key { get; set; }
    }
}