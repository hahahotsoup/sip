# hahahotsoup's rss reader

一个本地 RSS 订阅源管理工具，支持**版本追踪**、**快照归档**和**文章变化检测**。

## 功能

- **订阅管理**：添加、更新、删除 RSS 订阅源，所有数据存储在本地的
   SQLite 数据库中
- **文章追踪**：自动检测文章的新增、修改和删除，修改/删除的文章会
   保留历史版本（带时间戳归档标记），不会丢失
- **快照归档**：对订阅源加时间戳归档，保留某一时刻的完整快照，
   归档后的源不会被后续更新覆盖
- **差异展示**：更新时用文本 diff 直观显示文章条目变化
- **跨平台**：基于 .NET，数据存储为单个 `.db` 文件，Mac/Linux/Windows
   均可运行

## 快速开始

### 环境要求

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)

### 编译运行

```bash
git clone https://github.com/hahahotsoup/hahahotsoup-s-rssreader-core.git
cd hahahotsoup-s-rssreader-core
dotnet build
dotnet run
```

首次运行后，程序会在 exe 所在目录自动创建数据库文件。

### 测试用 RSS 服务器

项目附带了一个模拟 RSS 服务器 `rss_server.py`，方便测试：

```bash
pip install flask    # 如果使用 Flask 版本
python rss_server.py
```

然后在程序中输入 `http://localhost:5000/rss` 即可订阅。修改
`ARTICLES` 列表后重启服务器，再用程序更新就能看到 diff 效果。

## 使用说明

启动后显示主菜单：

```
A 看看已有订阅 | B 下载新RSS源 | Q 退出
```

### 订阅管理（A 菜单）

| 命令 | 示例 | 说明 |
|------|------|------|
| 输入编号 | `2` | 更新该订阅源，检测文章变化 |
| `T 编号` | `T 1` | 给订阅源标题加时间戳，归档当前快照 |
| `R 编号` | `R 1` | 去掉时间戳，恢复原始标题（检查同名冲突） |
| `D 编号` | `D 2` | 删除订阅源及其全部文章 |

### 下载新源（B 菜单）

直接输入 RSS 链接，自动下载、解析、存入数据库。

### 文章归档机制

程序对每篇文章维护三种状态：

| 状态 | 含义 |
|------|------|
| `active` | 当前有效 |
| `archived` | 作者修改后的旧版本（保留原文） |
| `deleted` | 作者删除了此文（标记但不物理删除） |

更新 RSS 时，程序会：

- 比对新旧 Content，**仅正文变化才触发归档**
- 修改的文章：旧版→`archived`，新版→`active`
- 删除的文章：`active`→`deleted`，附时间戳标记
- 列表显示各状态文章数量：`3 篇活跃, 1 篇旧版, 1 篇已删`

### 归档订阅源快照

`T 1` 后标题变为 `博客名_20260712_143000`，此后：

- 下载同名源会被当作**全新源**，不覆盖旧数据
- 归档源**禁止更新**，去归档需先执行 `R 1`
- 新下载的同名源获得独立的 FeedId 和文章副本

## 技术栈

- C# / .NET 10.0
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite)
- [CodeHollow.FeedReader](https://github.com/arminreiter/FeedReader)（RSS/Atom 解析）
- [DiffPlex](https://github.com/mmanela/diffplex)（文本差异比较）

## 项目结构

```
├── Hahahotsoup.RssReader.sln
├── Hahahotsoup.RssReader.Core.csproj
├── RssReader.cs          # 全部代码（单文件）
├── rss_server.py         # 测试用模拟RSS服务器
└── README.md
```
## AI使用情况
代码逻辑完全人工编写，AI仅辅助生成代码，不参与决策

## 许可证
遵循GNU General Public License v3.0 (GPL-3.0)
