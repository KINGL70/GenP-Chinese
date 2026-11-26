# GenP 可复用中文汉化补丁（zh-CN）

本目录提供一套**不依赖行号**的 AutoIt 词法级中文汉化工具，官方以后每次发布新版本（如 `v4.2.3`、`v4.3.0`）都可以直接复用，无需重新手工逐行翻译。

## 文件说明
- `zh-CN.json`：结构化中英对照词典（分为 `exact` 精确映射、`contextual` 上下文限定映射、`line_rules` 特殊语句映射，自动保护 INI 键名、注册表、逻辑判断不被误翻）。
- `apply_zh_cn.py`：一键汉化脚本（纯 Python 3，零第三方依赖；自动保存为 AutoIt 必需的 **UTF-8 with BOM** 编码，并校验引号、括号与代码块平衡）。

## 后续更新怎么用？

1. 把官方新版本源码解压到仓库根目录（例如 `v4.2.3/`）。
2. 运行一键汉化并检查是否有官方新增的英文界面文案：
   ```bash
   python3 i18n/apply_zh_cn.py v4.2.3/GenP/GenP-v4.2.3.au3 --report
   ```
3. 如果 `--report` 提示有几句官方新加的英文界面文字，把它们补进 `i18n/zh-CN.json`，再运行一次上面的命令即可。
4. 推送到你的 GitHub 仓库 `main` 分支，GitHub Actions（`.github/workflows/release.yml`）会自动在云端 Windows 环境中校验并编译出 **Windows 版中文 `.exe`** 发布到 Releases。

## 常用参数
- `--dry-run`：仅预览修改统计与语法校验，不改动文件
- `--report`：列出剩余未翻译的英文 UI 候选文本及行号
- `--out <路径>`：将汉化结果输出到指定文件，保留原文件不变
