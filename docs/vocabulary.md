# 词库与本地数据

内置词库是 **TEM-4 英语专业四级**，不是 CET-4 大学英语四级。固定来源为 `kajweb/dict` 的 `Level4_2`，上游 README 名称为“专四核心词汇（正序版）”。规范化脚本要求恰好 4025 个源记录和 4025 个不重复单词；数量不符会停止生成。

## 可追溯性与使用权

- 仓库：<https://github.com/kajweb/dict>
- 固定提交：`3992bcb94c800a2fd38a9fd6ff95b2353e755363`
- 数据路径：`book/1521164653685_Level4_2.zip`
- Git blob SHA-1：`bc12756e6f7b820aab9256cb88753cd0ce960fc3`
- 原始上游地址：<http://ydschool-online.nos.netease.com/1521164653685_Level4_2.zip>
- 生成资产：`assets/tem4.json`
- 归档 SHA-256、生成文件 SHA-256、实际记录数和例句覆盖数：`third-party/vocabulary-provenance.json`。

上游说明数据抓取自有道背单词 App。检查固定提交时未发现 LICENSE 文件，也未发现明示的再分发授权。本项目没有把该词库声明为 MIT、公共领域或其他开放许可；公开可下载不代表已取得再分发权。当前内置数据用于本地学习和测试；公开发行或商业分发前应取得相关权利人许可，或替换为具有明确授权的词库。程序源代码的许可不覆盖第三方词库内容。

运行 `scripts/fetch-vocabulary.ps1` 可从固定 URL 下载并校验 Git blob，再生成应用词库和校验清单。它仅用于开发；安装后的程序不运行此脚本，也没有运行时联网下载。网络下载失败会停止，不会用虚构单词补齐。

规范化只复制上游字段：英文 `headWord`，全部 `trans[].tranCn` 以分号连接，词性 `trans[].pos` 去重，音标优先美音 `usphone`、其次英音 `ukphone`、再其次 `phone`。例句只取上游第一条非空 `sContent` 及其对应 `sCn`；无例句就留空，不自动编造。保存原文中的重音和非 ASCII 字符。

## 导入格式

只接受 UTF-8（可带 UTF-8 BOM）编码的 `.json` 或 `.csv` 文件。文件上限 16 MB，单词上限 20000；英文与中文释义必填。英文最多 256 字符，中文 2048，词性 128，音标 256，英文和中文例句各 8192。失败时给出中文错误；整个文件验证通过后才写入，不会导入一半。

JSON 可以是词库对象或单词数组，字段名大小写不敏感：

```json
{
  "Name": "我的词库",
  "Source": "自行整理",
  "Words": [
    {
      "English": "apple",
      "Chinese": "苹果",
      "PartOfSpeech": "n.",
      "Phonetic": "ˈæpəl",
      "Example": "",
      "ExampleChinese": ""
    }
  ]
}
```

CSV 第一行必须含 `english,chinese`，可选列为 `partOfSpeech,phonetic,example,exampleChinese`，列顺序不限。支持 CRLF/LF、被双引号包裹的逗号或跨行字段、两个双引号表示一个引号。每条记录的列数应与表头一致。空行会跳过，多余列不参与词库内容。

```csv
english,chinese,partOfSpeech,phonetic,example,exampleChinese
apple,苹果,n.,ˈæpəl,,
```

导入时会裁剪字段首尾空白；同一词库按英文的 Unicode NFC 形式、忽略大小写去重，保留第一条。传入的单词或词库 ID 不作为文件路径或收藏依据，程序会重新计算。相同完整单词内容重复导入会更新已有词库；不同内容会成为独立词库。

## 数据位置与恢复

便携版（EXE 旁有 `portable.flag`）的数据目录为 `MoyuWord-x86\data`，与当前工作目录无关。安装版的数据目录仍为 `%LOCALAPPDATA%\MoyuWord`。两者都不需要管理员权限，但所在目录必须可写：

- `libraries\import-<hash>.json`：已经验证并保存的自定义词库。
- `favorites.json`：收藏的完整单词快照，因此删除原始导入文件不会丢失收藏。
- `settings.json`：透明度、置顶状态及窗口尺寸位置。
- `progress.json`：每个词库最后展示的词（从 0 开始的索引、英文及 UTC 时间），界面序号从 1 开始。内置、每个导入词库、收藏词库独立保存；恢复优先按英文找到原词，词已移除时把原索引限制到现有列表范围。
- 同名 `.bak`：原子替换时保留的上一版状态。

收藏按英文去重，因此同一个英文单词在不同词库共享收藏状态。收藏添加和删除立即保存；公开返回的收藏列表是深复制快照。写入使用同目录临时文件、磁盘刷新及原子替换；写入失败不会先改变内存收藏状态。完全相同的自定义词库只保存一份。

启动时损坏的自定义词库会跳过；损坏设置、收藏或进度会尝试 `.bak`，再回退至默认值。原始坏文件保持原样，诊断可从 `LibraryStore.Warnings` 取得。内置词库缺失是安装错误，会明确提示重新完整解压或安装。读取损坏状态不会自动覆盖原文件；用户后续正常保存才会更新相应状态。

## 存储验证

`tests/StoreTests.cs` 使用临时目录，验证 UTF-8 与 CSV 引号/换行、JSON 标识冲突、去重、收藏快照与重启、坏文件的完整性处理、大小限制、设置范围、备份恢复及全部内置专四词条。通过项目的 `scripts/test.ps1` 运行，测试不访问网络。
