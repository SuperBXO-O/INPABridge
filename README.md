# INPA Bridge

FFXIV 插件（Dalamud）。为 **INPA 前台帮手** 提供游戏内联动能力：

- 在内嵌浏览器里打开本机前台页面（`http://127.0.0.1:18500`）
- 取当前目标（`名字@服务器`）
- 代发聊天消息
- 读取背包
- 自动交易（放信物 / 金币）与交易事件回报

所有动作只在网页里点击时触发，不做后台自动操作。

## 安装

在游戏内输入 `/xlsettings` → **Experimental** → **Custom Plugin Repositories**，把下面这条链接加进去，保存：

```
https://raw.githubusercontent.com/SuperBXO-O/INPABridge/main/repo.json
```

然后 `/xlplugins` → 搜索 `INPA Bridge` → 安装。

## 前提

本插件是「桥」，自身不带前台页面。需要本机已运行 INPA 前台帮手网页服务，否则插件能装上但页面打不开。

## 命令

| 命令 | 作用 |
|---|---|
| `/inpa` | 打开/关闭主窗口 |
| `/inpalock` | 锁定/解锁窗口位置 |
| `/inpareload` | 重载内嵌页面 |
| `/inpacal` | 打开日历 |

## 环境

- Dalamud API Level **15**
- .NET **10**
- 客户端版本：FFXIV 7.5

API Level 不匹配的客户端无法安装本插件。

## 从源码构建

```bash
git clone https://github.com/SuperBXO-O/INPABridge.git
cd INPABridge
dotnet build -c Release
```

产物在 `bin/Release/`：`INPABridge.dll` + `INPABridge.json` + `INPABridge.deps.json`。把这三个文件打进一个 zip 就是插件包。

## 许可

AGPL-3.0，详见 [LICENSE](LICENSE)。

本插件参考了 Dalamud 的部分实现（同为 AGPL-3.0）。
