using System.Globalization;

namespace MaxScanning.Settings;

/// <summary>Bilingual UI strings. Keys are the English text; Chinese is looked up when the UI language is zh.</summary>
public static class L
{
    public static bool Zh =>
        Config.Current.Language == "zh" ||
        (string.IsNullOrEmpty(Config.Current.Language) && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh");

    public static string T(string en) => Zh && Map.TryGetValue(en, out var zh) ? zh : en;

    static readonly Dictionary<string, string> Map = new()
    {
        ["File"] = "文件", ["Edit"] = "编辑", ["View"] = "视图", ["Options"] = "选项", ["Window"] = "窗口", ["Help"] = "帮助",
        ["New scan..."] = "新建扫描...", ["New scan"] = "新建扫描", ["Open snapshot..."] = "打开快照...", ["Save snapshot..."] = "保存快照...",
        ["Export..."] = "导出...", ["Exit"] = "退出", ["Copy path"] = "复制路径", ["Copy name"] = "复制名称",
        ["Tag"] = "标签", ["Red"] = "红色", ["Green"] = "绿色", ["Blue"] = "蓝色", ["Yellow"] = "黄色", ["Clear tag"] = "清除标签", ["Clear all tags"] = "清除全部标签",
        ["Delete (Recycle Bin)"] = "删除（回收站）", ["Delete permanently"] = "永久删除", ["Back"] = "后退", ["Up one level"] = "转到上一层", ["Go to root"] = "转到根目录",
        ["Less detail"] = "精简信息", ["More detail"] = "更多信息", ["Show free space"] = "显示空闲空间", ["Show unknown space"] = "显示未知空间",
        ["Rescan"] = "重新扫描", ["Rescan this folder"] = "重新扫描此文件夹", ["Start watching"] = "开始监听", ["Stop watching"] = "停止监听", ["Event log console"] = "日志控制台",
        ["Configuration..."] = "配置...", ["Language"] = "语言", ["Restart as administrator (MFT turbo scan)"] = "以管理员身份重启（MFT 极速扫描）",
        ["New window on this view"] = "以当前视图新建窗口", ["Close window"] = "关闭窗口", ["Quick help"] = "快速帮助", ["Filter help"] = "过滤器帮助", ["About"] = "关于",
        ["Pause"] = "暂停", ["Resume"] = "继续", ["Stop"] = "停止", ["Filter"] = "过滤器", ["Apply"] = "应用", ["Clear"] = "清除",
        ["Open"] = "打开", ["Open in Explorer"] = "在资源管理器中显示", ["Open command prompt here"] = "在此处打开命令提示符", ["Properties"] = "属性", ["Zoom in"] = "放大进入",
        ["Choose a drive or enter a path"] = "选择一个驱动器或输入一个路径", ["Path:"] = "路径(&P):", ["Browse..."] = "浏览...", ["Start"] = "开始", ["Cancel"] = "取消", ["OK"] = "确定",
        ["Drive"] = "驱动器", ["Label"] = "卷标", ["File system"] = "文件系统", ["Total"] = "总容量", ["Free"] = "可用", ["Used"] = "已用",
        ["Scanning"] = "正在扫描", ["Ready"] = "就绪", ["files"] = "个文件", ["folders"] = "个文件夹", ["Size"] = "大小", ["Size on disk"] = "占用空间",
        ["Modified"] = "修改时间", ["Created"] = "创建时间", ["Accessed"] = "访问时间", ["Attributes"] = "属性", ["Path"] = "路径", ["of parent"] = "占上级",
        ["Free space"] = "可用空间", ["Unknown space"] = "未知空间", ["small items"] = "个小项目", ["Scan finished"] = "扫描完成", ["Scan cancelled"] = "扫描已取消",
        ["Graphics"] = "图形", ["Effects"] = "效果", ["Actions"] = "动作", ["Colors"] = "颜色",
        ["Minimum cell size (px)"] = "最小单元尺寸(像素)", ["Aspect ratio (left = tall, right = wide)"] = "比例（左=高，右=宽）", ["Initial detail level"] = "初始详细级别", ["Font size"] = "字体大小",
        ["Sort items by size"] = "按大小排序项目(&S)", ["Cushion shading"] = "立体阴影", ["Use size on disk instead of size"] = "使用占用空间代替文件大小",
        ["Refresh delay (ms, low = smooth)"] = "刷新延迟（毫秒，低=平滑）", ["Zoom time (ms)"] = "缩放时间（毫秒）", ["Dynamic zoom (relayout while zooming)"] = "使用动态缩放（缩放时实时调整内容）",
        ["Scan alternate data streams (slow)"] = "交替数据流扫描（慢）", ["Pop up event log console on new events"] = "新事件出现时弹出日志控制台", ["Log scan warnings"] = "日志记录扫描警告",
        ["Debug log"] = "调试日志", ["Flash window when scan completes"] = "扫描完成后闪动窗口", ["Keep tooltip when mouse moves between items"] = "鼠标在项目间移动时保持提示框",
        ["Use NTFS MFT turbo scan when possible"] = "尽可能使用 NTFS MFT 极速扫描", ["Confirm before delete"] = "删除前确认", ["Scan threads"] = "扫描线程数",
        ["Level contrast"] = "水平对比", ["Border contrast"] = "边界对比", ["Highlight on hover"] = "鼠标悬停高亮", ["Fade on mouse leave"] = "鼠标离开变淡", ["Highlight levels"] = "高亮层数",
        ["Folder"] = "文件夹", ["Stream"] = "数据流", ["Import..."] = "导入...", ["Export config..."] = "导出配置...", ["Defaults"] = "恢复默认",
        ["Export current view"] = "导出当前显示的内容", ["Preset"] = "预设", ["Header"] = "头部内容", ["Detail (one line per item)"] = "详细（每项一行）", ["Footer"] = "尾部内容",
        ["Sort"] = "排序", ["Max depth (0 = all)"] = "最大深度（0=全部）", ["Include files"] = "包含文件", ["Include folders"] = "包含文件夹", ["Minimum size"] = "最小大小",
        ["Preview (may show partial data)"] = "预览（可能只显示部分数据）", ["Save preset"] = "保存配置", ["Delete preset"] = "删除配置", ["Export to file..."] = "导出为文件...",
        ["Import presets..."] = "从文件中导入配置...", ["Export presets..."] = "导出配置到文件中...", ["Placeholders"] = "占位符", ["Escaping"] = "转义",
        ["Copy log"] = "复制日志", ["Clear log"] = "清空日志", ["Save log..."] = "保存日志...", ["Warning"] = "警告", ["Info"] = "信息",
        ["Watching for changes"] = "正在监听变化", ["Not elevated: using parallel scan. Restart as administrator for MFT turbo scan."] = "未以管理员运行：使用多线程扫描。以管理员身份重启可启用 MFT 极速扫描。",
        ["Delete"] = "删除", ["Are you sure you want to delete"] = "确定要删除", ["items"] = "项", ["Snapshot"] = "快照", ["Method"] = "方式", ["Elapsed"] = "耗时",
        ["Filter error"] = "过滤器错误", ["Nothing to show with the current filter"] = "当前过滤器下没有可显示的内容", ["Select a drive or folder to scan"] = "选择要扫描的驱动器或文件夹",
        ["Recent"] = "最近", ["Scan"] = "扫描", ["Volume"] = "卷", ["Memory"] = "内存", ["Hidden"] = "隐藏", ["System"] = "系统", ["Hard links"] = "硬链接",
        ["Size metric"] = "大小口径", ["Logical size"] = "文件大小", ["Allocated size"] = "占用空间", ["Breadcrumb"] = "路径", ["Escape"] = "转义",
        ["Unsorted"] = "不排序", ["Size (desc)"] = "大小（降序）", ["Size (asc)"] = "大小（升序）", ["Name (A-Z)"] = "名称（A-Z）", ["Name (Z-A)"] = "名称（Z-A）", ["Newest first"] = "最新优先",
        ["Preset name"] = "配置名称", ["Built-in presets cannot be deleted"] = "内置配置无法删除", ["Exported"] = "已导出", ["lines"] = "行",
        ["Access denied"] = "拒绝访问", ["Reparse point"] = "重解析点", ["Error"] = "错误", ["Done"] = "完成", ["Merging"] = "合并", ["Reading MFT"] = "读取 MFT",
        ["Enumerating"] = "枚举目录", ["Copy to clipboard"] = "复制到剪贴板", ["Building tree"] = "构建目录树", ["Rescan needed"] = "需要重新扫描", ["Tagged"] = "已标记",
    };
}
