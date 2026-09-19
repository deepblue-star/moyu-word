# 摸鱼单词

一个小巧的 Windows 离线悬浮单词卡 / PDF 阅读器。暖白与鼠尾草绿，鼠标移入时显示，移开后完全隐藏。开发分支为 `develop`。

## 使用

- 运行 `MoyuWord-Setup-x86.exe` 安装到当前用户；或解压 `MoyuWord-x86.zip`，运行 `MoyuWord.exe`。便携版请保留整个文件夹，包括 `assets`、`native`、`portable.flag` 和 `data`。
- 支持 Windows 10 32 位和 64 位，以及 Windows 11 64 位。程序是 **x86 32 位**，使用这些系统内置的 .NET Framework，PDF 引擎已随包携带；不用安装 Python、Node、Java、浏览器或开发工具。不承诺 Windows XP / 7 兼容。
- 鼠标移到窗口原位置即可唤回；移出后约 170ms 淡出。拖动顶部移动，拖动四边或角落调整尺寸。图钉切换置顶，置顶后仍然遵循离开隐藏。
- 底部「透明度」分别调节背景与文字，0% 为完全透明，100% 为完全可见。文字透明度同时作用于按钮、图标与 PDF 页面内容。透明处显示窗口下面的真实画面。
- **如果调得太透明找不到窗口：双击系统托盘图标恢复默认透明度**，或者窗口激活时按 `Ctrl + Shift + R`。任务栏可以重新唤回窗口。关闭叉号直接退出。
- 主页 → 背单词 → 开始背单词 → 选择词库。单词初始只露英文，点「我不认识」查看简洁释义、词性、音标及来源中提供的例句。左右按钮、方向键或在卡片区域横向拖动翻卡。
- 「收藏」进入错题本。错题本显示英文和中文，点击查看卡片详情，也可移出收藏。选择「收藏的单词」可以单独复习。
- 阅读 → 打开本地 PDF，使用上一页/下一页、加减缩放、适应宽度。PDF 文本和绘图在本机渲染，不启用 PDF JavaScript、表单或外部链接。加密 PDF 目前不提供密码输入；请使用未加密文件。

## 导入词库

在「背单词」或词库选择页点击「导入本地词库」。支持 UTF-8 JSON / CSV。示例见 [examples](examples)。英文和中文必填，其他内容可选。相同英文会去重；重新导入相同词库的规则见 [词库说明](docs/vocabulary.md)。

```json
{
  "Id": "my-words",
  "Name": "我的单词",
  "Words": [
    {
      "English": "pause",
      "Chinese": "暂停；停顿",
      "PartOfSpeech": "n. / v.",
      "Phonetic": "/pɔːz/",
      "Example": "Take a short pause.",
      "ExampleChinese": "稍作停顿。"
    }
  ]
}
```

## 数据与离线

**便携版**将导入词库、收藏、透明度及窗口位置保存到 EXE 旁的 `data` 文件夹。把整个 `MoyuWord-x86` 文件夹复制到 U 盘或另一台电脑，学习数据就会一起带走。请保留 `portable.flag`，并在拔出 U 盘前退出程序。U 盘不可写时会提示错误，不会改存到电脑用户目录。

**安装版**继续保存到 `%LOCALAPPDATA%\MoyuWord`，卸载保留这些学习数据。两种版本的数据独立，不自动合并。旧版用户可在退出程序后，把原数据目录的内容复制到便携版 `data`；如果两边都有数据，请先备份，勿直接覆盖。

重新构建会保留本地 `dist/MoyuWord-x86/data`；通用便携 ZIP 和安装包不携带个人数据。运行时没有网络请求、账号、云同步、更新检查或遥测。开发下载脚本不包含在运行逻辑中。

内置专四词库来自 `kajweb/dict` 的 `Level4_2`（**专四**，不是大学英语四级）。它是测试用学习数据；上游未提供清晰的再分发授权，**不代表本项目获得了商业词库授权**。具体来源、版本、数量和许可说明见 [词库说明](docs/vocabulary.md) 及 `third-party`，正式商业分发前请替换为有授权词库。

## 构建

在 Windows PowerShell 中：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
```

只使用系统 .NET Framework C# 编译器，不需要下载 NuGet 包或 .NET SDK。产物在 `dist`：安装 EXE、便携 ZIP 和可直接运行的文件夹。原生 PDFium 在 `native/pdfium.dll`；来源与校验信息见 `third-party`。

测试和本机安装验证记录见 [docs/verification.md](docs/verification.md)。尚未在真实 32 位 Windows 虚拟机上验证时，不将 x64 上的 WOW64 测试等同于完整的 32 位系统兼容性实测。
