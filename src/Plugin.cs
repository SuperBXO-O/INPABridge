// ============================================================================
//  INPA Bridge —— Dalamud 桥接插件（INPA 前台帮手的游戏联动层）
//
//  这份源码是 2026-10-11 从 INPABridge 1.6.4 的 DLL（ILSpy 9.1）反编译恢复的，
//  并修掉了反编译产物里无法通过编译的部分（功能未改动）：
//    · 指针解引用： ((T)ptr).X  →  ((T*)ptr)->X
//    · 数组初始化： RuntimeHelpers.InitializeArray(...) → Inventory1..4 显式赋值
//                    （已用原始 IL 的静态数组数据 {0,1,2,3} 核对一致）
//    · 类型歧义：   AtkValueType / Action 改为全限定名
//    · 委托订阅：   new OnXxxDelegate(...) → 直接写方法组
//
//  监听：127.0.0.1:18500
//  端点：/ping /target /send /notify /events /inventory /trade
//        /trade/log /audit /trade/nodes /app
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Bindings.ImGui;
using System.Numerics;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.NativeWrapper;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace INPABridge;

public sealed class Plugin : IDalamudPlugin, IDisposable
{
	private sealed class TradeEvent
	{
		public long Seq { get; set; }

		public string Type { get; set; } = "";

		public string Name { get; set; } = "";

		public long Amount { get; set; }

		public long Ts { get; set; }

		public string Text { get; set; } = "";
	}

	/// <summary>阶段0：收到的密聊（原样按顺序记下来，交给页面生成"工单"）。</summary>
	private sealed class TellRecord
	{
		public long Seq { get; set; }
		public string From { get; set; } = "";
		public string Text { get; set; } = "";
		public string Kind { get; set; } = "";
		public long Ts { get; set; }
	}

	private sealed class InvItem
	{
		public uint Id { get; set; }

		public string Name { get; set; } = "";

		public int Qty { get; set; }
	}

	private sealed class TradeReq
	{
		public string To = "";

		public List<(uint Id, int Qty)> Items = new List<(uint, int)>();

		public long Gil;
	}

	private sealed class FillSlot
	{
		public InventoryType Type;

		public int Slot;

		public int Want;
	}

	private const int Port = 18500;
	private const string InpaWebProfile = "inpa";
	private const string PrincetonWebProfile = "princeton";
	private const string GenericWebProfile = "generic";

	private static string? webRootInput;
	private static string webRootHint = "";
	private static bool webRootLoaded;
	private static int webProfileIndex = 2;

	private HttpListener? listener;

	private CancellationTokenSource? cts;

	private string boundPrefix = "";

	private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	private static readonly Regex WaitRx = new Regex("<\\s*wait\\.?\\s*([0-9]+(?:\\.[0-9]+)?)\\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly string[] PageRoutes = new string[4] { "/", "/app", "/app.html", "/index.html" };

	private static string? lastPageLogged;

	private static readonly object EvtLock = new object();

	private static readonly List<TradeEvent> Events = new List<TradeEvent>();

	/* ---- 阶段0：密聊工单 ---- */
	private static readonly List<TellRecord> Tells = new List<TellRecord>();
	private static readonly object TellLock = new object();
	private static long tellSeq;
	/* 每次插件加载换一个号：页面据此知道"插件重启了、序号重新计"，不会把新密聊当成旧的丢掉 */
	private static string TellRunId = DateTime.Now.ToString("yyyyMMddHHmmss");

	private static long evtSeq;

	private static readonly Regex RxTradeRequest = new Regex("收到了(.+?)发来的交易申请。", RegexOptions.Compiled);

	private static readonly Regex RxTradeConfirm = new Regex("等待(.+?)确认交易内容。", RegexOptions.Compiled);

	private const string AmountPattern = "[\\d,]+|[〇零一二三四五六七八九十百千万亿两壹贰貳叁叄肆伍陆柒捌玖拾佰仟]+";

	private static readonly Regex RxTradeReceive = new Regex("从对方那里收到了([\\d,]+|[〇零一二三四五六七八九十百千万亿两壹贰貳叁叄肆伍陆柒捌玖拾佰仟]+)金币。", RegexOptions.Compiled);

	private static readonly Regex RxTradeGive = new Regex("交给了对方([\\d,]+|[〇零一二三四五六七八九十百千万亿两壹贰貳叁叄肆伍陆柒捌玖拾佰仟]+)金币。", RegexOptions.Compiled);
	private static readonly Regex RxTellSendFailure = new Regex("向(.+?)发送(?:悄悄话|密语|私聊)失败", RegexOptions.Compiled);

	private static readonly Dictionary<char, long> CnDigits = new Dictionary<char, long>
	{
		['〇'] = 0L,
		['零'] = 0L,
		['一'] = 1L,
		['壹'] = 1L,
		['二'] = 2L,
		['两'] = 2L,
		['贰'] = 2L,
		['貳'] = 2L,
		['三'] = 3L,
		['叁'] = 3L,
		['叄'] = 3L,
		['四'] = 4L,
		['肆'] = 4L,
		['五'] = 5L,
		['伍'] = 5L,
		['六'] = 6L,
		['陆'] = 6L,
		['七'] = 7L,
		['柒'] = 7L,
		['八'] = 8L,
		['捌'] = 8L,
		['九'] = 9L,
		['玖'] = 9L
	};

	private static readonly Dictionary<char, long> CnSmallUnits = new Dictionary<char, long>
	{
		['十'] = 10L,
		['拾'] = 10L,
		['百'] = 100L,
		['佰'] = 100L,
		['千'] = 1000L,
		['仟'] = 1000L
	};

	private static TradeReq? pendingTrade;

	private static List<FillSlot>? fillQueue;

	private static int fillIndex;

	private static int fillPhase;

	private static int gilPhase;

	private static long gilAt;

	private static long gilWant;

	private static long phaseAt;

	private static bool menuOpened;
	private static long fillReadyAt;

	private static readonly List<string> fillLog = new List<string>();

	private static readonly List<string> auditLog = new List<string>();

	public string Name => "INPA Bridge";

	[PluginService]
	internal static IDalamudPluginInterface PluginInterface { get; private set; } = null;

	[PluginService]
	internal static ICommandManager CommandManager { get; private set; } = null;

	[PluginService]
	internal static ITargetManager TargetManager { get; private set; } = null;

	[PluginService]
	internal static IFramework Framework { get; private set; } = null;

	[PluginService]
	internal static IPluginLog Log { get; private set; } = null;

	[PluginService]
	internal static IChatGui ChatGui { get; private set; } = null;

	[PluginService]
	internal static IDataManager DataManager { get; private set; } = null;

	[PluginService]
	internal static IGameGui GameGui { get; private set; } = null;

	[PluginService]
	internal static IObjectTable ObjectTable { get; private set; } = null;

	/* 阶段0·区域限制要用到「当前在哪个区域」，所以也要拿到 ClientState */
	[PluginService]
	internal static IClientState ClientState { get; private set; } = null;

	public Plugin()
	{
		string[] array = new string[3]
		{
			$"http://127.0.0.1:{18500}/",
			$"http://localhost:{18500}/",
			$"http://[::1]:{18500}/"
		};
		foreach (string text in array)
		{
			HttpListener httpListener = new HttpListener
			{
				Prefixes = { text }
			};
			try
			{
				httpListener.Start();
				listener = httpListener;
				boundPrefix = text;
			}
			catch (Exception ex)
			{
				Log.Warning(ex, "INPA Bridge: 绑定 {0} 失败，试下一个…", new object[1] { text });
				try
				{
					httpListener.Close();
				}
				catch
				{
				}
				continue;
			}
			break;
		}
		if (listener == null)
		{
			Log.Error("INPA Bridge: 所有候选地址都绑定失败。端口 {0} 可能被占用，或缺少 URL 权限。", new object[1] { 18500 });
			return;
		}
		cts = new CancellationTokenSource();
		try
		{
			ChatGui.ChatMessage += OnChatMessage;
		}
		catch (Exception ex2)
		{
			Log.Warning(ex2, "INPA Bridge: 订阅聊天事件失败", Array.Empty<object>());
		}
		try
		{
			Framework.Update += OnFrameworkUpdate;
		}
		catch (Exception ex3)
		{
			Log.Warning(ex3, "INPA Bridge: 订阅帧事件失败", Array.Empty<object>());
		}
		try
		{
		PluginInterface.UiBuilder.Draw += DrawOverlayHook;
		PluginInterface.UiBuilder.Draw += DrawConfigWindow;
			try { PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi; } catch { }
		}
		catch (Exception ex5)
		{
			Log.Warning(ex5, "INPA Bridge: 挂输入窗口失败");
		}
		Task.Run(() => LoopAsync(cts.Token));
		Task.Run(() => RoomAlertLoopAsync(cts.Token));
		try
		{
			CommandManager.AddHandler("/inpa", new CommandInfo(OnInpaCommand)
			{
				HelpMessage = "INPA 前台帮手：显示 / 隐藏游戏内网页窗口（也可以用 /inpa show|hide|lock|reload|cal）"
			});
			CommandManager.AddHandler("/inpalock", new CommandInfo(OnInpaLockCommand)
			{
				HelpMessage = "INPA：切换游戏内窗口的位置锁定（锁定后网页里才能正常拖动、选字）"
			});
			CommandManager.AddHandler("/inpareload", new CommandInfo(OnInpaReloadCommand)
			{
				HelpMessage = "INPA：刷新游戏内网页"
			});
			CommandManager.AddHandler("/inpacal", new CommandInfo(OnInpaCalCommand)
			{
					HelpMessage = "INPA：输入框设置（对齐校准 / 颜色）"
			});
		}
		catch (Exception ex4)
		{
			Log.Warning(ex4, "INPA Bridge: 注册 /inpa 命令失败");
		}
		Log.Information("INPA Bridge 已启动，监听 {0}", new object[1] { boundPrefix });
	}

	public void Dispose()
	{
		try
		{
			ChatGui.ChatMessage -= OnChatMessage;
		}
		catch
		{
		}
		try
		{
			Framework.Update -= OnFrameworkUpdate;
		}
		catch
		{
		}
		try
		{
			CommandManager.RemoveHandler("/inpa");
			CommandManager.RemoveHandler("/inpalock");
			CommandManager.RemoveHandler("/inpareload");
			CommandManager.RemoveHandler("/inpacal");
		}
		catch
		{
		}
		try
		{
		try
		{
		PluginInterface.UiBuilder.Draw -= DrawOverlayHook;
		PluginInterface.UiBuilder.Draw -= DrawConfigWindow;
			try { PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi; } catch { }
		}
		catch
		{
		}
			cts?.Cancel();
		}
		catch
		{
		}
		try
		{
			listener?.Stop();
			listener?.Close();
		}
		catch
		{
		}
		Log.Information("INPA Bridge 已停止", Array.Empty<object>());
	}

	private async Task LoopAsync(CancellationToken token)
	{
		HttpListener l = listener;
		if (l == null)
		{
			return;
		}
		while (!token.IsCancellationRequested && l.IsListening)
		{
			HttpListenerContext ctx;
			try
			{
				ctx = await l.GetContextAsync().ConfigureAwait(continueOnCapturedContext: false);
			}
			catch
			{
				break;
			}
			Task.Run(() => Handle(ctx), token);
		}
	}

	private async Task Handle(HttpListenerContext ctx)
	{
		HttpListenerRequest request = ctx.Request;
		HttpListenerResponse res = ctx.Response;
		var origin = request.Headers["Origin"] ?? "";
		var pathNow = (request.Url?.AbsolutePath ?? "/").ToLowerInvariant();
		var webProfile = NormalizeWebProfile(ProfileName(request));
		var pingRoute = pathNow == "/ping";
		var pageRoute = IsPageRoute(request.Url?.AbsolutePath ?? "/");
		bool originOk = OriginAllowed(origin);
		bool tokenOk = TokenOk(request, webProfile);
		/* 页面正文里注入了本机口令，不能给跨源脚本读取权限；只允许导航打开。 */
		/* API 即使口令错误也回 CORS 头，让网页能读到明确的 403；真正动作仍会被下面的口令校验拦住。 */
		bool allowCrossOrigin = originOk || tokenOk || pingRoute || !pageRoute;
		if (origin.Length > 0 && allowCrossOrigin) { res.Headers["Access-Control-Allow-Origin"] = origin; res.Headers["Vary"] = "Origin"; }
		else if (origin.Length == 0) res.Headers["Access-Control-Allow-Origin"] = "*";   // 同源/本机程序
		/* 允许的头列全一点：页面里的 fetch 带 cache:"no-store" 时会额外加 Cache-Control/Pragma，
		   那会让请求变成"非简单请求"，浏览器先发 OPTIONS 预检——预检不过，真正的请求就发不出去。 */
		res.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-InPA-Token, X-InPA-Profile, Cache-Control, Pragma";
		res.Headers["Access-Control-Max-Age"] = "600";
		res.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
		/* Chromium 从公网 HTTPS 页面访问 127.0.0.1 时会先发 Private Network Access 预检。
		   不返回这一项时，浏览器表现为直接的 Failed to fetch。 */
		res.Headers["Access-Control-Allow-Private-Network"] = "true";
		try
		{
			if (request.HttpMethod == "OPTIONS")
			{
				res.StatusCode = allowCrossOrigin || origin.Length == 0 ? 204 : 403;
				res.Close();
				return;
			}
			/* 本机页面继续免口令；托管网页必须同时声明通用档案并携带该档案的口令。 */
			if (!pageRoute && !pingRoute && !originOk && !tokenOk)
			{
				res.StatusCode = 403;
				res.ContentType = "application/json; charset=utf-8";
				var msg = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"网页没有通过口令校验。请在插件「网页连接」页复制口令。\"}");
				res.OutputStream.Write(msg, 0, msg.Length);
				res.Close();
				return;
			}
			if (pageRoute)
			{
				await WritePageAsync(res, webProfile).ConfigureAwait(continueOnCapturedContext: false);
				return;
			}
			/* WebSocket 实时推送：握手成功后这条请求就交给 HandleWsAsync，不再写普通响应 */
			if ((request.Url?.AbsolutePath ?? "/").Equals("/ws", StringComparison.OrdinalIgnoreCase))
			{
				Log.Information("INPA Bridge: 收到实时推送连接请求（来源={0}）", new object[1] { string.IsNullOrEmpty(origin) ? "(无)" : origin });
				await HandleWsAsync(ctx).ConfigureAwait(continueOnCapturedContext: false);
				return;
			}
			object payload;
			switch ((request.Url?.AbsolutePath ?? "/").ToLowerInvariant())
			{
						case "/state":
				if (request.HttpMethod == "POST") { statePostCount++; lastStatePostAt = DateTime.Now.ToString("HH:mm:ss"); }
				else { stateGetCount++; lastStateGetAt = DateTime.Now.ToString("HH:mm:ss"); }
				payload = (request.HttpMethod == "POST")
					? await HandleStatePostAsync(request, webProfile).ConfigureAwait(false)
					: HandleStateGet(webProfile);
				break;

			case "/field":
				payload = (request.HttpMethod == "POST") ? await HandleFieldPostAsync(request).ConfigureAwait(false) : FieldState();
				break;

			case "/click":
				payload = await HandleClickPostAsync(request).ConfigureAwait(false);
				break;

			case "/modifier":
				payload = PhysicalModifierState();
				break;

			case "/export":
				payload = await HandleExportAsync(request, webProfile).ConfigureAwait(false);
				break;

			case "/sessions":
				payload = request.HttpMethod == "POST"
					? await HandleSessionsPostAsync(request, webProfile).ConfigureAwait(false)
					: HandleSessionsGet(request, webProfile);
				break;

			case "/sessions/archive":
				payload = await HandleSessionArchiveAsync(request, webProfile).ConfigureAwait(false);
				break;

			case "/sessions/file":
				payload = request.HttpMethod == "POST"
					? await HandleSessionFilePostAsync(request).ConfigureAwait(false)
					: HandleSessionFileGet(request);
				break;

			case "/sessions/delete":
				payload = await HandleSessionDeleteAsync(request).ConfigureAwait(false);
				break;

			case "/reveal":
				payload = HandleReveal(request);
				break;

			case "/pageinfo":
				payload = PageInfo(webProfile);
				break;

			case "/exportdir":
				if (request.HttpMethod == "POST") payload = await HandleExportDirPostAsync(request, webProfile).ConfigureAwait(false);
				else payload = new { ok = true, dir = ReadExportDir(webProfile) };
				break;
	
			case "/state/stats":
				payload = new { ok = true, get = stateGetCount, post = statePostCount, lastGet = lastStateGetAt, lastPost = lastStatePostAt, path = StateFile(webProfile) };
				break;
			case "/ping":
				payload = new
				{
					ok = true,
					name = "INPA Bridge",
					version = "1.7.90",
					api = 15,
					port = 18500,
					bound = boundPrefix
				};
				break;
			case "/target":
				payload = await GetTargetAsync().ConfigureAwait(continueOnCapturedContext: false);
				break;
			case "/send":
				payload = await HandleSendAsync(request).ConfigureAwait(continueOnCapturedContext: false);
				break;
			case "/notify":
				payload = await HandleNotifyAsync(request).ConfigureAwait(continueOnCapturedContext: false);
				break;
			case "/channels":
				payload = await HandleChannelsAsync().ConfigureAwait(continueOnCapturedContext: false);
				break;
			case "/room-status/send":
				payload = await HandleRoomStatusSendAsync(request).ConfigureAwait(continueOnCapturedContext: false);
				break;
			case "/events":
				payload = HandleEvents(request);
				break;
			case "/tells":
				payload = HandleTells(request);
				break;
			case "/tells/clear":
				/* 页面点了「开始新场次」：把插件手里这轮收到的密聊也清掉。
				   不清的话，页面刷新后会从序号 0 重新拉，把它们又变成工单。 */
				ResetTells("页面要求（开始新场次）");
				payload = new { ok = true, sid = TellRunId, max = tellSeq };
				break;
			case "/scene":
				payload = await RunOnFramework(() => HandleScene(request.QueryString["name"] ?? "")).ConfigureAwait(continueOnCapturedContext: false);
				break;
			case "/inventory":
				payload = await RunOnFramework(() => GetInventory()).ConfigureAwait(continueOnCapturedContext: false);
				break;
			case "/trade":
				payload = await HandleTradeAsync(request).ConfigureAwait(continueOnCapturedContext: false);
				break;
			case "/trade/log":
				payload = new
				{
					ok = true,
					phase = fillPhase,
					index = fillIndex,
					total = (fillQueue?.Count ?? 0),
					log = fillLog.ToArray(),
					audit = auditLog.ToArray()
				};
				break;
			case "/audit":
				payload = new
				{
					ok = true,
					audit = auditLog.ToArray()
				};
				break;
			case "/trade/nodes":
				payload = new
				{
					ok = true,
					dump = await RunOnFramework(() => DumpTradeNodes()).ConfigureAwait(continueOnCapturedContext: false)
				};
				break;
			default:
				res.StatusCode = 404;
				payload = new
				{
					ok = false,
					error = "unknown endpoint"
				};
				break;
			}
			await WriteJsonAsync(res, payload).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Log.Error(ex, "INPA Bridge: 处理请求失败", Array.Empty<object>());
			try
			{
				res.StatusCode = 500;
				await WriteJsonAsync(res, new
				{
					ok = false,
					error = ex.Message
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch
			{
			}
		}
	}

	private static async Task WriteJsonAsync(HttpListenerResponse res, object payload)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));
		res.ContentType = "application/json; charset=utf-8";
		res.ContentLength64 = bytes.Length;
		await res.OutputStream.WriteAsync(bytes).ConfigureAwait(continueOnCapturedContext: false);
		res.Close();
	}

	private static bool IsPageRoute(string path)
	{
		string[] pageRoutes = PageRoutes;
		foreach (string b in pageRoutes)
		{
			if (string.Equals(path, b, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static string ProfileName(HttpListenerRequest request)
	{
		var name = request.QueryString["profile"];
		if (string.IsNullOrWhiteSpace(name)) name = request.Headers["X-InPA-Profile"];
		return (name ?? "").Trim().ToLowerInvariant();
	}

	private static string NormalizeWebProfile(string? profile)
	{
		var value = (profile ?? "").Trim().ToLowerInvariant();
		if (value == InpaWebProfile) return InpaWebProfile;
		if (value == PrincetonWebProfile) return PrincetonWebProfile;
		if (value == GenericWebProfile) return GenericWebProfile;
		return "";
	}

	private static string WebProfileFolderName(string profile)
	{
		if (profile == InpaWebProfile) return "INPA Front Desk Helper";
		if (profile == PrincetonWebProfile) return "Princeton Front Desk Helper";
		if (profile == GenericWebProfile) return "FF14 RP Front Desk Helper";
		throw new InvalidOperationException("未知的网页档案");
	}

	private static string WebProfileLabel(string profile)
	{
		if (profile == InpaWebProfile) return "INPA";
		if (profile == PrincetonWebProfile) return "普林斯顿";
		if (profile == GenericWebProfile) return "通用版";
		return "网页";
	}

	private static string PluginDirectory()
	{
		try
		{
			FileInfo assemblyLocation = PluginInterface.AssemblyLocation;
			if (assemblyLocation != null && !string.IsNullOrEmpty(assemblyLocation.DirectoryName))
			{
				return assemblyLocation.DirectoryName;
			}
		}
		catch
		{
		}
		try
		{
			return Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? string.Empty;
		}
		catch
		{
			return string.Empty;
		}
	}

	/// <summary>当前页面所在的文件夹（也就是这个店自己的文件夹）。</summary>
	private static string? PageFolder()
	{
		try
		{
			var page = ResolvePagePath();
			if (page != null) return Path.GetDirectoryName(page);
		}
		catch { }
		return PluginDirectory();
	}

	private static string WebRootConfigFile(string profile)
	{
		var suffix = profile == GenericWebProfile ? "" : "-" + profile;
		return Path.Combine(PluginDirectory(), "web-data-root" + suffix + ".txt");
	}

	private static string DefaultWebRoot(string profile)
	{
		var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
		if (string.IsNullOrWhiteSpace(documents))
		{
			documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
		}
		return Path.Combine(documents, WebProfileFolderName(profile));
	}

	private static string WebDataRoot(string profile)
	{
		if (string.IsNullOrEmpty(profile)) throw new InvalidOperationException("缺少网页档案");
		try
		{
			var file = WebRootConfigFile(profile);
			if (File.Exists(file))
			{
				var configured = File.ReadAllText(file, Encoding.UTF8).Trim().Trim('"');
				if (configured.Length > 0 && Path.IsPathRooted(configured) && RootExists(configured))
					return Path.GetFullPath(configured);
			}
		}
		catch { }
		return DefaultWebRoot(profile);
	}

	private static string SaveWebRoot(string root, string profile)
	{
		var value = (root ?? "").Trim().Trim('"');
		if (value.Length == 0 || !Path.IsPathRooted(value) || !RootExists(value))
			throw new InvalidOperationException("请填写一个有效的绝对文件夹路径");
		value = Path.GetFullPath(value);
		Directory.CreateDirectory(value);
		File.WriteAllText(WebRootConfigFile(profile), value, new UTF8Encoding(false));
		return value;
	}

	/// <summary>页面把要导出的文件（xlsx / json）POST 过来，由插件写到磁盘——
	/// 游戏内的 CEF 弹不出"另存为"，所以走服务端落盘，并回报真实路径。
	/// dir 留空或不是绝对路径 → 写到"页面所在文件夹"（也就是这个店自己的文件夹）。</summary>
	private static async Task<object> HandleExportAsync(HttpListenerRequest request, string webProfile)
	{
		string body;
		using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
			body = await reader.ReadToEndAsync().ConfigureAwait(continueOnCapturedContext: false);

		string name = "", dir = "", b64 = "";
		try
		{
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement root = doc.RootElement;
			if (root.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String) name = nEl.GetString() ?? "";
			if (root.TryGetProperty("dir", out var dEl) && dEl.ValueKind == JsonValueKind.String) dir = dEl.GetString() ?? "";
			if (root.TryGetProperty("base64", out var bEl) && bEl.ValueKind == JsonValueKind.String) b64 = bEl.GetString() ?? "";
		}
		catch (Exception e) { return new { ok = false, error = "解析请求失败：" + e.Message }; }

		name = Path.GetFileName((name ?? "").Trim());
		if (string.IsNullOrEmpty(name)) return new { ok = false, error = "缺少文件名" };
		if (string.IsNullOrEmpty(b64)) return new { ok = false, error = "缺少文件内容" };
		byte[] bytes;
		try { bytes = Convert.FromBase64String(b64); }
		catch { return new { ok = false, error = "文件内容不是合法的 base64" }; }

		dir = (dir ?? "").Trim().Trim('"');
		// 留空 / 不是绝对路径 / 那个盘都不存在（换台电脑、目录没了）→ 一律回到「程序所在的文件夹」
		if (dir.Length == 0 || !Path.IsPathRooted(dir) || !RootExists(dir))
		{
			var folder = webProfile.Length > 0 ? WebDataRoot(webProfile) : PageFolder();
			if (string.IsNullOrEmpty(folder)) return new { ok = false, error = "找不到程序文件夹" };
			dir = folder!;
		}
		try
		{
			Directory.CreateDirectory(dir);
			string full = Path.Combine(dir, name);
			File.WriteAllBytes(full, bytes);
			Log.Information("INPA Bridge: 已导出到 {0}", new object[] { full });
			return new { ok = true, path = full };
		}
		catch (Exception e) { return new { ok = false, error = e.Message }; }
	}

	/// <summary>场次归档目录：指定绝对目录优先；否则用页面旁边的 data\sessions。</summary>
	private static string SessionDir(string? requested, string webProfile = "")
	{
		var dir = (requested ?? "").Trim().Trim('"');
		if (dir.Length > 0 && Path.IsPathRooted(dir) && RootExists(dir)) return dir;
		if (webProfile.Length > 0) return Path.Combine(WebDataRoot(webProfile), "data", "sessions");
		var folder = PageFolder();
		if (string.IsNullOrEmpty(folder)) throw new InvalidOperationException("找不到程序文件夹");
		return Path.Combine(folder!, "data", "sessions");
	}

	private static string SessionSummaryValue(JsonElement root, string objectName, string propertyName)
	{
		try
		{
			if (root.TryGetProperty(objectName, out var obj) && obj.ValueKind == JsonValueKind.Object &&
				obj.TryGetProperty(propertyName, out var value))
			{
				if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
				return value.GetRawText();
			}
		}
		catch { }
		return "";
	}

	/// <summary>列出一个目录里的本程序场次归档；只认 format=INPA_SESSION 的 JSON。</summary>
	private static object HandleSessionsGet(HttpListenerRequest request, string webProfile)
	{
		return BuildSessionsPayload(request.QueryString["dir"], webProfile);
	}

	private static async Task<object> HandleSessionsPostAsync(HttpListenerRequest request, string webProfile)
	{
		string body;
		using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
			body = await reader.ReadToEndAsync().ConfigureAwait(false);
		string dir = "";
		try
		{
			using var doc = JsonDocument.Parse(body);
			if (doc.RootElement.TryGetProperty("dir", out var dEl) && dEl.ValueKind == JsonValueKind.String)
				dir = dEl.GetString() ?? "";
		}
		catch { }
		return BuildSessionsPayload(dir, webProfile);
	}

	private static object BuildSessionsPayload(string? requestedDir, string webProfile)
	{
		try
		{
			var dir = SessionDir(requestedDir, webProfile);
			Directory.CreateDirectory(dir);
			var list = new List<object>();
			foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
				.OrderByDescending(x => File.GetLastWriteTimeUtc(x)))
			{
				try
				{
					using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
					var root = doc.RootElement;
					if (!root.TryGetProperty("format", out var fmt) || fmt.ValueKind != JsonValueKind.String ||
						!string.Equals(fmt.GetString(), "INPA_SESSION", StringComparison.Ordinal)) continue;
					var baseName = Path.GetFileNameWithoutExtension(file);
					var xlsx = Path.Combine(dir, baseName + ".xlsx");
					list.Add(new
					{
						name = baseName,
						jsonPath = file,
						xlsxPath = xlsx,
						startedAt = SessionSummaryValue(root, "session", "startedAt"),
						endedAt = SessionSummaryValue(root, "session", "endedAt"),
						shop = root.TryGetProperty("shop", out var shop) && shop.ValueKind == JsonValueKind.String ? (shop.GetString() ?? "") : "",
						salesCount = SessionSummaryValue(root, "summary", "salesCount"),
						settlementCount = SessionSummaryValue(root, "summary", "settlementCount"),
						revenue = SessionSummaryValue(root, "summary", "revenue"),
						netProfit = SessionSummaryValue(root, "summary", "netProfit"),
						updated = File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm:ss")
					});
				}
				catch { }
			}
			return new { ok = true, dir, sessions = list.ToArray() };
		}
		catch (Exception e) { return new { ok = false, error = e.Message }; }
	}

	private static string? FindArchivedJsonById(string dir, string id)
	{
		if (string.IsNullOrWhiteSpace(id)) return null;
		foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
		{
			try
			{
				using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
				var root = doc.RootElement;
				if (!root.TryGetProperty("format", out var fmt) || fmt.ValueKind != JsonValueKind.String ||
					!string.Equals(fmt.GetString(), "INPA_SESSION", StringComparison.Ordinal)) continue;
				var got = SessionSummaryValue(root, "session", "id");
				if (string.Equals(got, id, StringComparison.Ordinal)) return file;
			}
			catch { }
		}
		return null;
	}

	private static string UniqueArchiveBase(string dir, string wanted)
	{
		var name = ArchiveStem(wanted);
		if (name.Length == 0) name = "前台营业流水";
		var candidate = name;
		for (int i = 2; ; i++)
		{
			if (!File.Exists(Path.Combine(dir, candidate + ".json")) &&
				!File.Exists(Path.Combine(dir, candidate + ".xlsx"))) return candidate;
			candidate = name + "-" + i;
		}
	}

	private static string ArchiveStem(string? name)
	{
		var value = Path.GetFileName((name ?? "").Trim());
		if (value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
			value.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
			value = value.Substring(0, value.Length - 5);
		return value;
	}

	/// <summary>写一对同名 JSON + XLSX；同一个 session.id 再次保存会覆盖同一对文件。</summary>
	private static async Task<object> HandleSessionArchiveAsync(HttpListenerRequest request, string webProfile)
	{
		string body;
		using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
			body = await reader.ReadToEndAsync().ConfigureAwait(false);

		string dirRequested = "", name = "", json = "", b64 = "";
		try
		{
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;
			if (root.TryGetProperty("dir", out var dEl) && dEl.ValueKind == JsonValueKind.String) dirRequested = dEl.GetString() ?? "";
			if (root.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String) name = nEl.GetString() ?? "";
			if (root.TryGetProperty("json", out var jEl) && jEl.ValueKind == JsonValueKind.String) json = jEl.GetString() ?? "";
			if (root.TryGetProperty("base64", out var bEl) && bEl.ValueKind == JsonValueKind.String) b64 = bEl.GetString() ?? "";
		}
		catch (Exception e) { return new { ok = false, error = "解析请求失败：" + e.Message }; }

		if (string.IsNullOrWhiteSpace(json)) return new { ok = false, error = "缺少场次 JSON" };
		if (string.IsNullOrWhiteSpace(b64)) return new { ok = false, error = "缺少 XLSX 内容" };

		string sessionId = "";
		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			if (!root.TryGetProperty("format", out var fmt) || fmt.ValueKind != JsonValueKind.String ||
				!string.Equals(fmt.GetString(), "INPA_SESSION", StringComparison.Ordinal))
				return new { ok = false, error = "不是 INPA_SESSION 场次文件" };
			sessionId = SessionSummaryValue(root, "session", "id");
		}
		catch (Exception e) { return new { ok = false, error = "场次 JSON 无效：" + e.Message }; }

		byte[] xlsx;
		try { xlsx = Convert.FromBase64String(b64); }
		catch { return new { ok = false, error = "XLSX 内容不是合法的 base64" }; }

		try
		{
			var dir = SessionDir(dirRequested, webProfile);
			Directory.CreateDirectory(dir);
			var existing = FindArchivedJsonById(dir, sessionId);
			var baseName = existing != null
				? Path.GetFileNameWithoutExtension(existing)
				: UniqueArchiveBase(dir, name);
			var jsonPath = Path.Combine(dir, baseName + ".json");
			var xlsxPath = Path.Combine(dir, baseName + ".xlsx");
			WriteDurableFile(xlsxPath, xlsx);
			/* JSON 是“归档完成”的提交标记：先写 Excel，最后写 JSON，崩溃时不会列出一条缺表记录。 */
			WriteDurableFile(jsonPath, new UTF8Encoding(false).GetBytes(json));
			Log.Information("INPA Bridge: 已归档场次 {0}", new object[] { baseName });
			return new { ok = true, name = baseName, jsonPath, xlsxPath, dir };
		}
		catch (Exception e) { return new { ok = false, error = e.Message }; }
	}

	private static object HandleSessionFileGet(HttpListenerRequest request)
	{
		return BuildSessionFilePayload((request.QueryString["path"] ?? "").Trim().Trim('"'));
	}

	private static async Task<object> HandleSessionFilePostAsync(HttpListenerRequest request)
	{
		string body;
		using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
			body = await reader.ReadToEndAsync().ConfigureAwait(false);
		string path = "";
		try
		{
			using var doc = JsonDocument.Parse(body);
			if (doc.RootElement.TryGetProperty("path", out var pEl) && pEl.ValueKind == JsonValueKind.String)
				path = pEl.GetString() ?? "";
		}
		catch { }
		return BuildSessionFilePayload(path.Trim().Trim('"'));
	}

	private static object BuildSessionFilePayload(string path)
	{
		try
		{
			if (path.Length == 0 || !Path.IsPathRooted(path) ||
				!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase) ||
				!File.Exists(path))
				return new { ok = false, error = "场次文件不存在" };
			return new { ok = true, path, content = File.ReadAllText(path, Encoding.UTF8) };
		}
		catch (Exception e) { return new { ok = false, error = e.Message }; }
	}

	private static async Task<object> HandleSessionDeleteAsync(HttpListenerRequest request)
	{
		string body;
		using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
			body = await reader.ReadToEndAsync().ConfigureAwait(false);
		string jsonPath = "";
		try
		{
			using var doc = JsonDocument.Parse(body);
			if (doc.RootElement.TryGetProperty("jsonPath", out var pEl) && pEl.ValueKind == JsonValueKind.String)
				jsonPath = (pEl.GetString() ?? "").Trim().Trim('"');
		}
		catch (Exception e) { return new { ok = false, error = "解析请求失败：" + e.Message }; }

		try
		{
			if (jsonPath.Length == 0 || !Path.IsPathRooted(jsonPath) ||
				!string.Equals(Path.GetExtension(jsonPath), ".json", StringComparison.OrdinalIgnoreCase))
				return new { ok = false, error = "场次文件不存在" };

			var dir = Path.GetDirectoryName(jsonPath);
			if (string.IsNullOrEmpty(dir)) return new { ok = false, error = "场次目录无效" };
			var baseName = Path.GetFileNameWithoutExtension(jsonPath);
			var xlsxPath = Path.Combine(dir!, baseName + ".xlsx");
			if (!File.Exists(jsonPath))
			{
				bool removedOrphan = false;
				if (File.Exists(xlsxPath)) { File.Delete(xlsxPath); removedOrphan = true; }
				return new { ok = true, missing = true, jsonPath, xlsxPath, xlsxDeleted = removedOrphan };
			}

			using (var doc = JsonDocument.Parse(ReadStateText(jsonPath)))
			{
				if (!doc.RootElement.TryGetProperty("format", out var fmt) || fmt.ValueKind != JsonValueKind.String ||
					!string.Equals(fmt.GetString(), "INPA_SESSION", StringComparison.Ordinal))
					return new { ok = false, error = "拒绝删除：不是 INPA_SESSION 场次文件" };
			}

			/* Excel 先删；JSON 是归档完成标记，最后删。任何一步失败都会保留仍可定位的 JSON。 */
			bool xlsxDeleted = false;
			if (File.Exists(xlsxPath)) { File.Delete(xlsxPath); xlsxDeleted = true; }
			File.Delete(jsonPath);
			return new { ok = true, jsonPath, xlsxPath, xlsxDeleted };
		}
		catch (Exception e) { return new { ok = false, error = e.Message }; }
	}

	/// <summary>这个路径所在的盘存在吗（换了台电脑就可能没有 D: 了）。</summary>
	private static bool RootExists(string dir)
	{
		try
		{
			var root = Path.GetPathRoot(dir);
			return !string.IsNullOrEmpty(root) && Directory.Exists(root);
		}
		catch { return false; }
	}

	/// <summary>在资源管理器里选中刚导出的文件。</summary>
	private static object HandleReveal(HttpListenerRequest request)
	{
		string body;
		using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
			body = reader.ReadToEnd();
		string path = "";
		try
		{
			using JsonDocument doc = JsonDocument.Parse(body);
			if (doc.RootElement.TryGetProperty("path", out var pEl) && pEl.ValueKind == JsonValueKind.String) path = pEl.GetString() ?? "";
		}
		catch { }
		if (string.IsNullOrWhiteSpace(path)) return new { ok = false, error = "缺少路径" };
		try
		{
			if (Directory.Exists(path))
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
			else if (File.Exists(path))
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
			return new { ok = true };
		}
		catch (Exception e) { return new { ok = false, error = e.Message }; }
	}

	private static string? ResolvePagePath()
	{
		string text = PluginDirectory();
		if (string.IsNullOrEmpty(text))
		{
			return null;
		}
		List<string> list = new List<string>();
		try
		{
			string path = Path.Combine(text, "page.txt");
			if (File.Exists(path))
			{
				string text2 = File.ReadAllText(path).Trim().Trim('"', '\ufeff');
				if (text2.Length > 0)
				{
					if (Directory.Exists(text2))
					{
						list.Add(Path.Combine(text2, "app.html"));
						list.Add(Path.Combine(text2, "index.html"));
					}
					else
					{
						list.Add(text2);
					}
				}
			}
		}
		catch
		{
		}
		list.Add(Path.Combine(text, "app.html"));
		list.Add(Path.Combine(text, "index.html"));
		list.Add(Path.Combine(text, "www", "app.html"));
		list.Add(Path.Combine(text, "www", "index.html"));
		foreach (string item in list)
		{
			try
			{
				if (File.Exists(item))
				{
					return item;
				}
			}
			catch
			{
			}
		}
		return null;
	}

	private static string? ResolvePagePathForProfile(string webProfile)
	{
		if (string.IsNullOrEmpty(webProfile)) return ResolvePagePath();
		var expectedVariant = webProfile == InpaWebProfile ? "main"
			: webProfile == PrincetonWebProfile ? "princeton"
			: "generic";
		var candidates = new List<string>();
		void Add(string? path)
		{
			if (string.IsNullOrWhiteSpace(path)) return;
			if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase)) candidates.Add(path);
		}

		var current = ResolvePagePath();
		Add(current);
		try
		{
			var webRoot = WebDataRoot(webProfile);
			Add(Path.Combine(webRoot, "app.html"));
			Add(Path.Combine(webRoot, "index.html"));
			Add(Path.Combine(webRoot, "上传用", "index.html"));
		}
		catch { }
		var pluginDir = PluginDirectory();
		Add(Path.Combine(pluginDir, "app.html"));
		Add(Path.Combine(pluginDir, "index.html"));
		Add(Path.Combine(pluginDir, "www", "app.html"));
		Add(Path.Combine(pluginDir, "www", "index.html"));
		Add(Path.Combine(pluginDir, "www", webProfile, "app.html"));
		Add(Path.Combine(pluginDir, "www", webProfile, "index.html"));
		try
		{
			var currentFolder = string.IsNullOrEmpty(current) ? null : Path.GetDirectoryName(current);
			var parent = string.IsNullOrEmpty(currentFolder) ? null : Path.GetDirectoryName(currentFolder);
			if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
			{
				foreach (var dir in Directory.GetDirectories(parent))
				{
					Add(Path.Combine(dir, "app.html"));
					Add(Path.Combine(dir, "上传用", "index.html"));
				}
			}
		}
		catch { }

		foreach (var candidate in candidates)
		{
			try
			{
				if (!File.Exists(candidate)) continue;
				var html = File.ReadAllText(candidate, Encoding.UTF8);
				if (Regex.IsMatch(html,
					"<html[^>]*data-fd-variant\\s*=\\s*\"" + Regex.Escape(expectedVariant) + "\"",
					RegexOptions.IgnoreCase))
					return candidate;
			}
			catch { }
		}
		return null;
	}

	private static string NotFoundHtml()
	{
		string text = PluginDirectory();
		string text2 = WebUtility.HtmlEncode(text);
		string text3 = WebUtility.HtmlEncode(Path.Combine(text, "page.txt"));
		return "<!doctype html><html lang=\"zh\"><head><meta charset=\"utf-8\"><title>INPA 前台帮手 · 未找到页面</title></head><body style=\"background:#1a1420;color:#e8e0f0;font:14px/1.9 'Microsoft YaHei',sans-serif;padding:40px\"><h2 style=\"color:#e0b563\">没有找到 app.html 或 index.html</h2><p>把 <b>app.html</b> 或 <b>index.html</b> 放到下面这个目录，然后刷新本页：</p><pre style=\"background:#241d2b;padding:12px;border-radius:8px;white-space:pre-wrap\">" + text2 + "</pre><p>也可以创建 <b>page.txt</b>（与插件 DLL 同目录），写入页面文件完整路径，或写入页面所在文件夹路径。</p><pre style=\"background:#241d2b;padding:12px;border-radius:8px;white-space:pre-wrap\">" + text3 + "</pre><p style=\"color:#9a8fb0\">文件示例：D:\\你放这个文件夹的位置\\index.html<br>目录示例：D:\\你放这个文件夹的位置</p></body></html>";
	}

	private async Task WritePageAsync(HttpListenerResponse res, string webProfile)
	{
		string? path = ResolvePagePathForProfile(webProfile);
		byte[] array;
		if (path == null)
		{
			if (lastPageLogged != "<none>")
			{
				lastPageLogged = "<none>";
				Log.Warning("INPA Bridge: 没找到 app.html 或 index.html。请放到 {0}，或在插件目录创建 page.txt 写上页面文件或文件夹路径。", new object[1] { PluginDirectory() });
			}
			res.StatusCode = 404;
			array = Encoding.UTF8.GetBytes(NotFoundHtml());
		}
		else
		{
			if (path != lastPageLogged)
			{
				lastPageLogged = path;
				Log.Information("INPA Bridge: 页面来源 {0}", new object[1] { path });
			}
			try
			{
				array = await File.ReadAllBytesAsync(path).ConfigureAwait(continueOnCapturedContext: false);
				array = InjectPageStamp(array, path, webProfile);
			}
			catch (Exception ex)
			{
				Log.Error(ex, "INPA Bridge: 读取页面失败 {0}", new object[1] { path });
				res.StatusCode = 500;
				array = Encoding.UTF8.GetBytes("读取 app.html 失败：" + ex.Message);
			}
		}
		res.ContentType = "text/html; charset=utf-8";
		res.AddHeader("Cache-Control", "no-store, no-cache, must-revalidate");
		res.ContentLength64 = array.Length;
		await res.OutputStream.WriteAsync(array).ConfigureAwait(continueOnCapturedContext: false);
		res.Close();
	}

	/// <summary>给发出去的页面注入一个「页面版本戳」（就是 app.html 的修改时间）。
	/// 页面把它和 /pageinfo 返回的当前戳一比：不一样 = 浏览器手里那份是旧的 → 提示刷新。
	/// 这样改了 app.html 之后，开着的旧窗口自己会说话，不用猜为什么点了没反应。</summary>
	private static byte[] InjectPageStamp(byte[] bytes, string path, string webProfile)
	{
		try
		{
			string stamp = File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture);
			/* 顺便把访问口令注入页面：本机模式（游戏内窗口 / 启动器发的页面）用起来无感，
			   页面会自动把它带在每个请求上。 */
			string inject = "<script>window.__INPA_PAGE_STAMP=\"" + stamp + "\";window.__INPA_TOKEN=\"" + AccessToken(webProfile) + "\";</script>";
			string html = Encoding.UTF8.GetString(bytes);
			/* 必须插在页面自己的脚本之前 —— 页面是"一加载就读口令"，插到 </body> 前就太晚了。 */
			int at = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
			if (at >= 0) html = html.Substring(0, at + 6) + inject + html.Substring(at + 6);
			else
			{
				int at2 = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
				html = (at2 >= 0) ? (html.Substring(0, at2) + inject + html.Substring(at2)) : (html + inject);
			}
			return Encoding.UTF8.GetBytes(html);
		}
		catch { return bytes; }
	}

	// ---------------------------------------------------------------- 导出目录（本机设置，不进同步数据）
	// 以前存在共用业务数据里，两个窗口一同步/一冲突重载就可能被覆盖成空。
	// 现在单独存成「页面所在文件夹」里的 exportdir.txt —— 跟店走、不参与同步。
	private static string ExportDirFile(string webProfile = "")
	{
		if (webProfile.Length > 0) return Path.Combine(WebDataRoot(webProfile), "exportdir.txt");
		var folder = PageFolder();
		if (string.IsNullOrEmpty(folder)) folder = PluginDirectory();
		return Path.Combine(folder!, "exportdir.txt");
	}

	private static string ReadExportDir(string webProfile = "")
	{
		try
		{
			var f = ExportDirFile(webProfile);
			if (File.Exists(f)) return File.ReadAllText(f, Encoding.UTF8).Trim();
		}
		catch { }
		return "";
	}

	private static object WriteExportDir(string dir, string webProfile = "")
	{
		try
		{
			dir = (dir ?? "").Trim().Trim('"');
			var file = ExportDirFile(webProfile);
			var parent = Path.GetDirectoryName(file);
			if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
			File.WriteAllText(file, dir, new UTF8Encoding(false));
			Log.Information("INPA Bridge: 导出目录已设为 [{0}]", new object[] { dir });
			return new { ok = true, dir };
		}
		catch (Exception e) { return new { ok = false, error = e.Message }; }
	}

	private static async Task<object> HandleExportDirPostAsync(HttpListenerRequest request, string webProfile)
	{
		string body;
		using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
			body = await reader.ReadToEndAsync().ConfigureAwait(continueOnCapturedContext: false);
		string dir = "";
		try
		{
			using JsonDocument doc = JsonDocument.Parse(body);
			if (doc.RootElement.TryGetProperty("dir", out var dEl) && dEl.ValueKind == JsonValueKind.String) dir = dEl.GetString() ?? "";
		}
		catch { }
		return WriteExportDir(dir, webProfile);
	}

	/// <summary>当前页面的版本信息（给页面判断自己是不是旧的）。</summary>
	private static object PageInfo(string webProfile)
	{
		string p = "", stamp = "";
		try
		{
			p = ResolvePagePathForProfile(webProfile) ?? "";
			if (p.Length > 0 && File.Exists(p))
				stamp = File.GetLastWriteTimeUtc(p).Ticks.ToString(CultureInfo.InvariantCulture);
		}
		catch { }
		if (webProfile.Length > 0)
		{
			var dataRoot = Path.Combine(WebDataRoot(webProfile), "data");
			return new
			{
				ok = true,
				stamp,
				path = p,
				profile = webProfile,
				profileLabel = WebProfileLabel(webProfile),
				dataRoot,
				sessionDir = Path.Combine(dataRoot, "sessions"),
				statePath = Path.Combine(dataRoot, "inpa-data.json")
			};
		}
		return new { ok = true, stamp, path = p };
	}

	private Task<object> GetTargetAsync()
	{
		return RunOnFramework((Func<object>)delegate
		{
			IGameObject target = TargetManager.Target;
			if (target == null)
			{
				Audit("取目标：当前没有选中任何目标");
				return new
				{
					ok = false,
					reason = "no-target",
					name = "",
					world = "",
					full = ""
				};
			}
			string text = target.Name.TextValue ?? string.Empty;
			string text2 = WorldNameOf(target);
			Audit("取目标：读出当前目标「" + text + (string.IsNullOrEmpty(text2) ? "" : ("@" + text2)) + "」");
			return new
			{
				ok = !string.IsNullOrEmpty(text),
				reason = (string.IsNullOrEmpty(text) ? "empty-name" : ""),
				name = text,
				world = text2,
				full = (string.IsNullOrEmpty(text2) ? text : (text + "@" + text2))
			};
		});
	}

	private static string WorldNameOf(IGameObject obj)
	{
		IPlayerCharacter val = (IPlayerCharacter)(object)((obj is IPlayerCharacter) ? obj : null);
		if (val == null)
		{
			return string.Empty;
		}
		try
		{
			RowRef<World> homeWorld = val.HomeWorld;
			if (homeWorld.RowId == 0)
			{
				return string.Empty;
			}
			World value = homeWorld.Value;
			return value.Name.ToString();
		}
		catch (Exception ex)
		{
			Log.Warning(ex, "INPA Bridge: 读取区服名失败", Array.Empty<object>());
			return string.Empty;
		}
	}

	/* ---------------------------------------------------------------- 发送节奏（防抖）
	   游戏对聊天发送有节流：前台连着点几个人「排队」时，每条消息都在自己的后台任务里发，
	   光靠页面那边防抖没用，发太快游戏会直接不认（密聊发不出去）。
	   所以统一在这里排个队：任意两条要发出去的聊天命令之间至少隔 1.5 秒。 */
	private static readonly object SendGate = new object();
	private static long nextSendAt;                 // Environment.TickCount64
	private const int SendGapMs = 1500;
	private const int RoomStatusLineGapMs = 150;

	private static async Task WaitSendSlot()
	{
		long at;
		lock (SendGate)
		{
			long now = Environment.TickCount64;
			at = Math.Max(now, nextSendAt);
			nextSendAt = at + SendGapMs;
		}
		int wait = (int)Math.Max(0L, at - Environment.TickCount64);
		if (wait > 0)
		{
			await Task.Delay(wait).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private async Task<object> HandleSendAsync(HttpListenerRequest req)
	{
		string text;
		using (StreamReader reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
		{
			text = await reader.ReadToEndAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
		string text2 = null;
		string text3 = null;
		if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("{"))
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(text);
			JsonElement rootElement = jsonDocument.RootElement;
			if (rootElement.TryGetProperty("to", out var value))
			{
				text2 = value.GetString();
			}
			if (rootElement.TryGetProperty("text", out var value2))
			{
				text3 = value2.GetString();
			}
		}
		else
		{
			text2 = req.QueryString["to"];
			text3 = req.QueryString["text"];
		}
		if (string.IsNullOrWhiteSpace(text3))
		{
			return new
			{
				ok = false,
				error = "text 为空",
				to = ""
			};
		}
		string text4 = (text2 ?? string.Empty).Trim();
		if (text4.Length == 0 || text4 == "@target")
		{
			text4 = await RunOnFramework(delegate
			{
				IGameObject target = TargetManager.Target;
				if (target == null)
				{
					return string.Empty;
				}
				string text5 = target.Name.TextValue ?? string.Empty;
				string text6 = WorldNameOf(target);
				return (!string.IsNullOrEmpty(text6)) ? (text5 + "@" + text6) : text5;
			}).ConfigureAwait(continueOnCapturedContext: false);
			if (string.IsNullOrEmpty(text4))
			{
				return new
				{
					ok = false,
					error = "游戏内没有选中目标",
					to = ""
				};
			}
		}
		List<(string Line, int DelayMs)> lines = SplitScript(text3, text4);
		if (lines.Count == 0)
		{
			return new
			{
				ok = false,
				error = "没有可发送的内容",
				to = text4
			};
		}
		Task.Run(async delegate
		{
			int lineNo = 0;
			foreach (var item2 in lines)
			{
				string item = item2.Line;
				int delayMs = item2.DelayMs;
				string cmd = item;
				lineNo++;
				try
				{
					await WaitSendSlot().ConfigureAwait(continueOnCapturedContext: false);
					await RunOnFramework(delegate
					{
						SendToChatBox(cmd);
						return 0;
					}).ConfigureAwait(continueOnCapturedContext: false);
					/* 发出去的每行都留个时间戳：节流后如果有消息晚了几秒，日志里能看出是被排在谁后面 */
					Log.Information("INPA Bridge: 已发送（{0}/{1}）→ {2}", new object[3] { lineNo, lines.Count, cmd });
				}
				catch (Exception ex)
				{
					Log.Error(ex, "INPA Bridge: 发送失败 -> {0}", new object[1] { cmd });
				}
				await Task.Delay((delayMs > 0) ? delayMs : 350).ConfigureAwait(continueOnCapturedContext: false);
			}
		});
		Audit("直接发送 →「" + text4 + "」，共 " + lines.Count + " 行");
		return new
		{
			ok = true,
			to = text4,
			lines = lines.Count
		};
	}

	private async Task<object> HandleNotifyAsync(HttpListenerRequest req)
	{
		string text;
		using (StreamReader reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
		{
			text = await reader.ReadToEndAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
		string text2 = null;
		string text3 = null;
		if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("{"))
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(text);
			JsonElement rootElement = jsonDocument.RootElement;
			if (rootElement.TryGetProperty("text", out var value))
			{
				text2 = value.GetString();
			}
			if (rootElement.TryGetProperty("channel", out var value2))
			{
				text3 = value2.GetString();
			}
		}
		else
		{
			text2 = req.QueryString["text"];
			text3 = req.QueryString["channel"];
		}
		if (string.IsNullOrWhiteSpace(text2))
		{
			return new
			{
				ok = false,
				error = "text 为空"
			};
		}
		string cmd = (string.IsNullOrWhiteSpace(text3) ? "/echo" : text3.Trim());
		string line = cmd + " " + text2.Trim();
		Audit("默语提醒 → " + cmd);
		return new
		{
			ok = await RunOnFramework(() => SendToChatBox(line)).ConfigureAwait(continueOnCapturedContext: false),
			channel = cmd
		};
	}

	private unsafe Task<object> HandleChannelsAsync()
	{
		return RunOnFramework((Func<object>)delegate
		{
			List<object> channels = new List<object>();
			InfoModule* infoModule = InfoModule.Instance();
			InfoProxyCrossWorldLinkshell* proxy = infoModule == null
				? null
				: infoModule->GetInfoProxyCrossWorldLinkshell();
			for (int i = 0; i < 8; i++)
			{
				string name = "";
				if (proxy != null)
				{
					try { name = proxy->CrossWorldLinkshells[i].Name.ToString() ?? ""; }
					catch (Exception ex) { Log.Debug(ex, "INPA Bridge: 读取跨服贝 {0} 名称失败", new object[1] { i + 1 }); }
				}
				channels.Add(new
				{
					id = "cwl" + (i + 1),
					slot = i + 1,
					command = "/cwl" + (i + 1),
					name = name
				});
			}
			return new { ok = true, channels = channels };
		});
	}

	private async Task<object> HandleRoomStatusSendAsync(HttpListenerRequest req)
	{
		string raw;
		using (StreamReader reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
		{
			raw = await reader.ReadToEndAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
		string channel = "";
		List<string> lines = new List<string>();
		try
		{
			using JsonDocument doc = JsonDocument.Parse(raw);
			if (doc.RootElement.TryGetProperty("channel", out JsonElement channelElement))
			{
				channel = (channelElement.GetString() ?? "").Trim().ToLowerInvariant();
			}
			if (doc.RootElement.TryGetProperty("lines", out JsonElement lineElement) &&
				lineElement.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in lineElement.EnumerateArray())
				{
					string line = (item.GetString() ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
					if (line.Length > 0) lines.Add(line);
					if (lines.Count >= 200) break;
				}
			}
		}
		catch (JsonException)
		{
			return new { ok = false, error = "请求内容不是有效 JSON" };
		}
		if (!Regex.IsMatch(channel, "^cwl[1-8]$", RegexOptions.CultureInvariant))
		{
			return new { ok = false, error = "只能发送到跨服贝 1～8" };
		}
		if (lines.Count == 0)
		{
			return new { ok = false, error = "没有可发送的包间信息" };
		}
		string command = "/" + channel;
		Audit("包间情况 → " + command + "，共 " + lines.Count + " 行（按游戏节流逐行发送）");
		bool allSent = true;
		int lineNo = 0;
		foreach (string line in lines)
		{
			lineNo++;
			try
			{
				if (lineNo > 1)
				{
					await Task.Delay(RoomStatusLineGapMs).ConfigureAwait(continueOnCapturedContext: false);
				}
				bool sent = await RunOnFramework(() => SendToChatBox(command + " " + line)).ConfigureAwait(continueOnCapturedContext: false);
				if (!sent) allSent = false;
				Log.Information("INPA Bridge: 包间情况已发送（{0}/{1}）→ {2} {3}",
					new object[4] { lineNo, lines.Count, command, line });
			}
			catch (Exception ex)
			{
				allSent = false;
				Log.Error(ex, "INPA Bridge: 包间情况发送失败 -> {0} {1}", new object[2] { command, line });
			}
		}
		return new
		{
			ok = allSent,
			channel = command,
			lines = lines.Count,
			throttleMs = RoomStatusLineGapMs,
			error = allSent ? "" : "部分包间信息发送失败"
		};
	}

	private static List<(string Line, int DelayMs)> SplitScript(string text, string target)
	{
		List<(string, int)> list = new List<(string, int)>();
		text = text.Replace("<t>", target).Replace("<T>", target);
		List<(bool, string)> list2 = new List<(bool, string)>();
		int num = 0;
		foreach (Match item in WaitRx.Matches(text))
		{
			if (item.Index > num)
			{
				list2.Add((false, text.Substring(num, item.Index - num)));
			}
			list2.Add((true, item.Groups[1].Value));
			num = item.Index + item.Length;
		}
		if (num < text.Length)
		{
			list2.Add((false, text.Substring(num)));
		}
		foreach (var (flag, text2) in list2)
		{
			if (flag)
			{
				if (list.Count > 0 && double.TryParse(text2, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
				{
					(string, int) tuple2 = list[list.Count - 1];
					list[list.Count - 1] = (tuple2.Item1, (int)(result * 1000.0));
				}
				continue;
			}
			string[] array = text2.Split('\n');
			for (int i = 0; i < array.Length; i++)
			{
				string text3 = array[i].Trim();
				if (text3.Length != 0)
				{
					if (!text3.StartsWith("/", StringComparison.Ordinal))
					{
						text3 = "/tell " + target + " " + text3;
					}
					list.Add((text3, 0));
				}
			}
		}
		return list;
	}

	private unsafe static bool SendToChatBox(string command)
	{
		try
		{
			UIModule* ptr = UIModule.Instance();
			if (ptr == null)
			{
				return false;
			}
			Utf8String* ptr2 = Utf8String.FromString(command);
			if (ptr2 == null)
			{
				return false;
			}
			try
			{
				((UIModule*)ptr)->ProcessChatBoxEntry(ptr2, (IntPtr)0, false);
			}
			finally
			{
				((Utf8String*)ptr2)->Dtor();
			}
			return true;
		}
		catch (Exception ex)
		{
			Log.Error(ex, "INPA Bridge: 发送聊天命令失败 -> {0}", new object[1] { command });
			return false;
		}
	}

	private static bool TryRecordSendFailure(string text)
	{
		Match match = RxTellSendFailure.Match(text ?? "");
		if (!match.Success) return false;
		string target = match.Groups[1].Value.Trim().Trim('「', '」');
		if (target.Length == 0) return false;
		long ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
		long seq;
		lock (TellLock)
		{
			tellSeq++;
			seq = tellSeq;
			Tells.Add(new TellRecord
			{
				Seq = tellSeq,
				From = target,
				Text = text,
				Kind = "sendfail",
				Ts = ts
			});
			if (Tells.Count > 500) Tells.RemoveRange(0, Tells.Count - 500);
		}
		Log.Information("INPA Bridge: 密聊发送失败 {0} → {1}", new object[2] { target, text });
		WsBroadcast("{\"t\":\"tell\",\"sid\":" + JsonStr(TellRunId) + ",\"seq\":" + seq + ",\"from\":" + JsonStr(target) +
			",\"text\":" + JsonStr(text) + ",\"kind\":\"sendfail\",\"ts\":" + ts + "}");
		return true;
	}

	private void OnChatMessage(IHandleableChatMessage msg)
	{
		try
		{
			var m = (IMutableChatMessage)msg;
			string text = m.Message.TextValue ?? string.Empty;
			if (TryRecordSendFailure(text)) return;
			/* 阶段0：把「收到的密聊」按顺序记下来，页面会拿去生成工单（只看收到的，自己发出去的不记）。 */
			if (m.LogKind == XivChatType.TellIncoming)
			{
				try
				{
					string from = ((m.Sender == null) ? "" : (m.Sender.TextValue ?? "")).Trim();
					if (from.Length > 0)
					{
						/* 区域限制：不是房屋场景里的实体的密聊，不算工单（日志里留个痕，方便排查） */
						if (!InHousingTerritory())
						{
							Log.Information("INPA Bridge: 密聊没记工单（当前不在房屋场景，area={0}）：{1} → {2}", new object[3] { TerritoryIntendedUseNow(), from, text });
						}
						else
						{
							IGameObject obj = MatchScenePlayer(from);
							if (obj == null)
							{
								/* 把「当时场景里都能看到谁」一起记下来，免得只在日志里干瞪眼 */
								List<string> list = ScenePlayerNames();
								Log.Information("INPA Bridge: 密聊没记工单（房屋里看不到这个实体；当时可见玩家 {0} 人：{1}；框架线程={2}）：{3} → {4}",
									new object[5]
									{
										list.Count,
										string.Join("、", list.Take(8).ToArray()),
										Framework.IsInFrameworkUpdateThread,
										from,
										text
									});
							}
							else
							{
								/* 用对象表里的真名 + 区服覆盖密聊里那串（可能带好友图标 / 用游戏符号接区服），
								   这样页面上的「排队」直接用 /tell 就能发出去，跨服客人也不会报「需要指定服务器名」 */
								string who = CanonicalName(obj, from);
								long ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
								long seq;
								lock (TellLock)
								{
									tellSeq++;
									seq = tellSeq;
									Tells.Add(new TellRecord
									{
										Seq = tellSeq,
										From = who,
										Text = text,
										Ts = ts
									});
									if (Tells.Count > 500) Tells.RemoveRange(0, Tells.Count - 500);
								}
								Log.Information("INPA Bridge: 收到密聊 {0} → {1}", new object[2] { who, text });
								/* 实时推给开着的页面（页面按 sid+序号去重，重复推也没关系） */
								WsBroadcast("{\"t\":\"tell\",\"sid\":" + JsonStr(TellRunId) + ",\"seq\":" + seq + ",\"from\":" + JsonStr(who) + ",\"text\":" + JsonStr(text) + ",\"ts\":" + ts + "}");
							}
						}
					}
				}
				catch (Exception te) { Log.Debug(te, "INPA Bridge: 记录密聊失败"); }
			}
			if (text.Length != 0 && (text.Contains("交易") || text.Contains("对方")))
			{
				string type = "raw";
				string name = "";
				long amount = 0L;
				Match match = RxTradeRequest.Match(text);
				if (match.Success)
				{
					type = "request";
					name = match.Groups[1].Value.Trim();
				}
				else if ((match = RxTradeConfirm.Match(text)).Success)
				{
					type = "confirm";
					name = match.Groups[1].Value.Trim();
				}
				else if ((match = RxTradeReceive.Match(text)).Success)
				{
					type = "receive";
					amount = ParseAmount(match.Groups[1].Value);
				}
				else if ((match = RxTradeGive.Match(text)).Success)
				{
					type = "give";
					amount = ParseAmount(match.Groups[1].Value);
				}
				else if (text.Contains("交易完成"))
				{
					type = "done";
				}
				PushEvent(type, name, amount, text);
			}
		}
		catch (Exception ex)
		{
			Log.Debug(ex, "INPA Bridge: 处理聊天消息失败", Array.Empty<object>());
		}
	}

	private static long ParseAmount(string s)
	{
		string text = s.Replace(",", "").Replace(" ", "").Trim();
		if (text.Length == 0)
		{
			return 0L;
		}
		if (long.TryParse(text, out var result))
		{
			return result;
		}
		if (!TryParseChineseNumber(text, out var value))
		{
			return 0L;
		}
		return value;
	}

	private static bool TryParseChineseNumber(string s, out long value)
	{
		value = 0L;
		long num = 0L;
		long num2 = 0L;
		long num3 = 0L;
		bool result = false;
		foreach (char c in s)
		{
			switch (c)
			{
			case '0':
			case '1':
			case '2':
			case '3':
			case '4':
			case '5':
			case '6':
			case '7':
			case '8':
			case '9':
				num3 = num3 * 10 + (c - 48);
				result = true;
				continue;
			case ' ':
			case '\u00a0':
				continue;
			}
			if (CnDigits.TryGetValue(c, out var value2))
			{
				num3 = value2;
				result = true;
				continue;
			}
			if (CnSmallUnits.TryGetValue(c, out var value3))
			{
				if (num3 == 0L && value3 == 10)
				{
					num3 = 1L;
				}
				num2 += num3 * value3;
				num3 = 0L;
				result = true;
				continue;
			}
			switch (c)
			{
			case '万':
				num2 = (num2 + num3) * 10000;
				num += num2;
				num2 = 0L;
				num3 = 0L;
				result = true;
				break;
			case '亿':
				num2 = (num2 + num3) * 100000000;
				num += num2;
				num2 = 0L;
				num3 = 0L;
				result = true;
				break;
			default:
				return false;
			}
		}
		value = num + num2 + num3;
		return result;
	}

	private static void PushEvent(string type, string name, long amount, string text)
	{
		TradeEvent evt;
		lock (EvtLock)
		{
			evt = new TradeEvent
			{
				Seq = ++evtSeq,
				Type = type,
				Name = name,
				Amount = amount,
				Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				Text = text
			};
			Events.Add(evt);
			if (Events.Count > 800)
			{
				Events.RemoveRange(0, Events.Count - 800);
			}
		}
		/* 实时推给页面（形状和 /events 里的一条一样，页面按 seq 去重） */
		WsBroadcast("{\"t\":\"event\",\"ev\":{\"seq\":" + evt.Seq + ",\"type\":" + JsonStr(evt.Type) + ",\"name\":" + JsonStr(evt.Name) + ",\"amount\":" + evt.Amount + ",\"text\":" + JsonStr(evt.Text) + ",\"ts\":" + evt.Ts + "}}");
	}

	/// <summary>阶段0：清空密聊记录（换店铺页面时用）。
	/// 密聊记录是「这一轮跑出来的」临时数据，不是业务数据：切到别的店还不清的话，
	/// 新店页面第一次拉 /tells 会把上一个店收到的密聊读成一堆工单。
	/// 顺带换一个会话号，页面看到 sid 变了就知道从 0 重新对账。</summary>
	private static void ResetTells(string why)
	{
		lock (TellLock)
		{
			Tells.Clear();
			tellSeq = 0;
		}
		TellRunId = DateTime.Now.ToString("yyyyMMddHHmmss");
		Log.Information("INPA Bridge: 已清空密聊记录（{0}）", new object[1] { why });
	}

	/// <summary>阶段0：把收到的密聊按顺序交给页面（?since=N 表示只要比它新的）。</summary>
	private static object HandleTells(HttpListenerRequest req)
	{
		long since = 0;
		try { long.TryParse(req.QueryString["since"] ?? "0", out since); } catch { }
		List<TellRecord> list;
		long max;
		lock (TellLock)
		{
			max = tellSeq;
			list = Tells.Where(delegate (TellRecord t) { return t.Seq > since; }).ToList();
		}
		return new
		{
			ok = true,
			sid = TellRunId,
			max = max,
			/* 诊断用：当前区域类型 / 是不是房屋场景（页面用不到，方便排查「密聊怎么没进工单」） */
			area = areaUse,
			housing = areaHousing,
			tells = list.Select(delegate (TellRecord t)
			{
				return new { seq = t.Seq, from = t.From, text = t.Text, kind = t.Kind, ts = t.Ts };
			}).ToArray()
		};
	}

	private static object HandleEvents(HttpListenerRequest req)
	{
		long.TryParse(req.QueryString["since"], out var since);
		long last;
		List<TradeEvent> events;
		lock (EvtLock)
		{
			last = evtSeq;
			events = Events.Where((TradeEvent e) => e.Seq > since).ToList();
		}
		return new
		{
			ok = true,
			last = last,
			events = events
		};
	}

	private unsafe static object GetInventory()
	{
		Dictionary<uint, int> dictionary = new Dictionary<uint, int>();
		InventoryManager* ptr = InventoryManager.Instance();
		if (ptr != null)
		{
			InventoryType[] array = new InventoryType[4];
			array[0] = InventoryType.Inventory1; array[1] = InventoryType.Inventory2; array[2] = InventoryType.Inventory3; array[3] = InventoryType.Inventory4;
			InventoryType[] array2 = (InventoryType[])(object)array;
			foreach (InventoryType val in array2)
			{
				InventoryContainer* inventoryContainer = ((InventoryManager*)ptr)->GetInventoryContainer(val);
				if (inventoryContainer == null)
				{
					continue;
				}
				for (int j = 0; j < ((InventoryContainer*)inventoryContainer)->Size; j++)
				{
					InventoryItem* inventorySlot = ((InventoryContainer*)inventoryContainer)->GetInventorySlot(j);
					if (inventorySlot == null)
					{
						continue;
					}
					uint itemId = ((InventoryItem*)inventorySlot)->ItemId;
					if (itemId != 0)
					{
						int quantity = ((InventoryItem*)inventorySlot)->Quantity;
						if (quantity > 0)
						{
							dictionary[itemId] = (dictionary.TryGetValue(itemId, out var value) ? (value + quantity) : quantity);
						}
					}
				}
			}
		}
		ExcelSheet<Item> excelSheet = DataManager.GetExcelSheet<Item>((ClientLanguage?)null, (string)null);
		List<InvItem> list = new List<InvItem>();
		foreach (KeyValuePair<uint, int> item in dictionary)
		{
			string name = "";
			try
			{
				Item row = excelSheet.GetRow(item.Key);
				name = row.Name.ToString();
			}
			catch
			{
			}
			list.Add(new InvItem
			{
				Id = item.Key,
				Name = name,
				Qty = item.Value
			});
		}
		list.Sort((InvItem a, InvItem b) => string.CompareOrdinal(a.Name, b.Name));
		return new
		{
			ok = true,
			count = list.Count,
			items = list
		};
	}

	private static void Flog(string m)
	{
		lock (fillLog)
		{
			fillLog.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + m);
			if (fillLog.Count > 300)
			{
				fillLog.RemoveAt(0);
			}
		}
		Log.Information("INPA Bridge[trade]: {0}", new object[1] { m });
	}

	private static void Audit(string m)
	{
		lock (auditLog)
		{
			auditLog.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + m);
			if (auditLog.Count > 600)
			{
				auditLog.RemoveAt(0);
			}
		}
		Log.Information("INPA Bridge[audit]: {0}", new object[1] { m });
	}

	private unsafe static AtkUnitBase* FindAddonVisible(string[] names, int maxIndex = 8)
	{
		foreach (string text in names)
		{
			for (int j = 1; j <= maxIndex; j++)
			{
				AtkUnitBasePtr addonByName = GameGui.GetAddonByName(text, j);
				if (addonByName.Address != (IntPtr)IntPtr.Zero)
				{
					AtkUnitBase* address = (AtkUnitBase*)addonByName.Address;
					if (((AtkUnitBase*)address)->IsVisible)
					{
						return address;
					}
				}
			}
		}
		return null;
	}

	private unsafe static bool IsTradeOpen()
	{
		return FindAddonVisible(new string[2] { "Trade", "TradeWindow" }) != null;
	}

	private unsafe static string DumpTradeNodes()
	{
		try
		{
			AtkUnitBase* ptr = FindAddonVisible(new string[2] { "Trade", "TradeWindow" });
			if (ptr == null)
			{
				return "(交易窗口没打开)";
			}
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.Append("visible=").Append(((AtkUnitBase*)ptr)->IsVisible).Append(" atkValues=")
				.Append(((AtkUnitBase*)ptr)->AtkValuesCount)
				.Append(" nodes:");
			AtkResNode** nodeList = ((AtkUldManager*)(&((AtkUnitBase*)ptr)->UldManager))->NodeList;
			ushort nodeListCount = ((AtkUldManager*)(&((AtkUnitBase*)ptr)->UldManager))->NodeListCount;
			for (int i = 0; i < nodeListCount && i < 200; i++)
			{
				if (nodeList == null)
				{
					break;
				}
				AtkResNode* ptr2 = nodeList[i];
				if (ptr2 == null)
				{
					continue;
				}
				stringBuilder.Append(" [").Append(((AtkResNode*)ptr2)->NodeId).Append(":")
					.Append((int)((AtkResNode*)ptr2)->Type);
				if (NodeHasClickEvent(ptr2))
				{
					stringBuilder.Append("!");
				}
				if ((int)((AtkResNode*)ptr2)->Type == 3)
				{
					try
					{
						string value = ((object)(*(Utf8String*)(&((AtkTextNode*)ptr2)->NodeText))/*cast due to .constrained prefix*/).ToString();
						if (!string.IsNullOrEmpty(value))
						{
							stringBuilder.Append(":").Append(value);
						}
					}
					catch
					{
					}
				}
				else if ((int)((AtkResNode*)ptr2)->Type == 5)
				{
					try
					{
						string value2 = ((object)(*(Utf8String*)(&((AtkCounterNode*)ptr2)->NodeText))/*cast due to .constrained prefix*/).ToString();
						if (!string.IsNullOrEmpty(value2))
						{
							stringBuilder.Append(":").Append(value2);
						}
					}
					catch
					{
					}
				}
				stringBuilder.Append("]");
			}
			return stringBuilder.ToString();
		}
		catch (Exception ex)
		{
			return "(dump 失败: " + ex.Message + ")";
		}
	}

	private unsafe static void DriveGil()
	{
		long tickCount = Environment.TickCount64;
		if (!IsTradeOpen())
		{
			Flog("金币：交易窗口已关闭，停止");
			gilPhase = 0;
			pendingTrade = null;
		}
		else if (FindAddonVisible(new string[2] { "Trade", "TradeWindow" }) == null)
		{
			gilPhase = 0;
		}
		else
		{
			if (tickCount - gilAt < 300)
			{
				return;
			}
			int tradeGilAmount = (int)Math.Min(gilWant, 1000000L);
			try
			{
				InventoryManager* ptr = InventoryManager.Instance();
				if (ptr != null)
				{
					((InventoryManager*)ptr)->SetTradeGilAmount((uint)tradeGilAmount);
					Audit("自动填金币：SetTradeGilAmount(" + tradeGilAmount + ") 已生效（请手动确认交易）");
					Flog("已设置交易金币 " + tradeGilAmount);
					gilPhase = 0;
					pendingTrade = null;
				}
			}
			catch (Exception ex)
			{
				Audit("自动填金币：SetTradeGilAmount 失败 " + ex.Message + "，请手动输入");
				gilPhase = 0;
				pendingTrade = null;
			}
		}
	}

	private unsafe static bool NodeHasClickEvent(AtkResNode* n)
	{
		try
		{
			return ((AtkResNode*)n)->IsEventRegistered((AtkEventType)25) || ((AtkResNode*)n)->IsEventRegistered((AtkEventType)9);
		}
		catch
		{
			return false;
		}
	}

	private unsafe static int FindContextEntry(AgentInventoryContext* agent, string startsWith)
	{
		int num = Math.Min(((AgentInventoryContext*)agent)->ContextItemCount, 64);
		for (int i = 0; i < num; i++)
		{
			AtkValue val = ((AgentInventoryContext*)agent)->EventParams[((AgentInventoryContext*)agent)->ContexItemStartIndex + i];
			if (((int)val.Type == 8 || (int)val.Type == 40) && val.String.Value != null)
			{
				string text = Marshal.PtrToStringUTF8((nint)val.String.Value);
				if (!string.IsNullOrWhiteSpace(text) && text.Trim().StartsWith(startsWith, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}
		}
		return -1;
	}

	private unsafe static void FireContextEntry(AtkUnitBase* unit, int idx)
	{
		AtkValue* ptr = (AtkValue*)Marshal.AllocHGlobal(5 * Unsafe.SizeOf<AtkValue>());
		try
		{
			Unsafe.Write(&((AtkValue*)ptr)->Type, (FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType)3);
			((AtkValue*)ptr)->Int = 0;
			Unsafe.Write(&((AtkValue*)((byte*)ptr + Unsafe.SizeOf<AtkValue>()))->Type, (FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType)3);
			((AtkValue*)((byte*)ptr + Unsafe.SizeOf<AtkValue>()))->Int = idx;
			Unsafe.Write(&((AtkValue*)((byte*)ptr + (nint)2 * (nint)Unsafe.SizeOf<AtkValue>()))->Type, (FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType)5);
			((AtkValue*)((byte*)ptr + (nint)2 * (nint)Unsafe.SizeOf<AtkValue>()))->UInt = 0u;
			Unsafe.Write(&((AtkValue*)((byte*)ptr + (nint)3 * (nint)Unsafe.SizeOf<AtkValue>()))->Type, (FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType)3);
			((AtkValue*)((byte*)ptr + (nint)3 * (nint)Unsafe.SizeOf<AtkValue>()))->Int = 0;
			Unsafe.Write(&((AtkValue*)((byte*)ptr + (nint)4 * (nint)Unsafe.SizeOf<AtkValue>()))->Type, (FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType)3);
			((AtkValue*)((byte*)ptr + (nint)4 * (nint)Unsafe.SizeOf<AtkValue>()))->Int = 0;
			((AtkUnitBase*)unit)->FireCallback(5u, ptr, false);
		}
		finally
		{
			Marshal.FreeHGlobal((nint)ptr);
		}
	}

	private async Task<object> HandleTradeAsync(HttpListenerRequest req)
	{
		string json;
		using (StreamReader r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
		{
			json = await r.ReadToEndAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
		string to = "";
		long gil = 0L;
		List<(uint, int)> items = new List<(uint, int)>();
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			JsonElement rootElement = jsonDocument.RootElement;
			if (rootElement.TryGetProperty("to", out var value))
			{
				to = value.GetString() ?? "";
			}
			if (rootElement.TryGetProperty("gil", out var value2) && value2.ValueKind == JsonValueKind.Number)
			{
				gil = value2.GetInt64();
			}
			if (rootElement.TryGetProperty("items", out var value3) && value3.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in value3.EnumerateArray())
				{
					JsonElement value4;
					uint num = (item.TryGetProperty("id", out value4) ? value4.GetUInt32() : 0u);
					JsonElement value5;
					int num2 = (item.TryGetProperty("qty", out value5) ? value5.GetInt32() : 0);
					if (num != 0 && num2 > 0)
					{
						items.Add((num, num2));
					}
				}
			}
		}
		catch (Exception ex)
		{
			return new
			{
				ok = false,
				error = "bad json: " + ex.Message
			};
		}
		string toName = to.Trim();
		int num3 = toName.IndexOf('@');
		if (num3 > 0)
		{
			toName = toName.Substring(0, num3);
		}
		if (toName.Length == 0 || toName == "@target")
		{
			toName = (await RunOnFramework(delegate
			{
				IGameObject target = TargetManager.Target;
				if (target == null)
				{
					return string.Empty;
				}
				string text2 = target.Name.TextValue ?? string.Empty;
				string text3 = WorldNameOf(target);
				string text4 = (string.IsNullOrEmpty(text3) ? text2 : (text2 + "@" + text3));
				int num5 = text4.IndexOf('@');
				return (num5 <= 0) ? text4 : text4.Substring(0, num5);
			}).ConfigureAwait(continueOnCapturedContext: false)) ?? string.Empty;
		}
		lock (fillLog)
		{
			fillLog.Clear();
		}
		gilPhase = 0;
		pendingTrade = new TradeReq
		{
			To = toName,
			Items = items,
			Gil = gil
		};
		Audit("收到交易请求：to=" + ((toName.Length > 0) ? toName : "(空)") + "，物品 " + items.Count + " 种，需付金币 " + gil);
		fillQueue = null;
		fillIndex = 0;
		menuOpened = false;
		fillReadyAt = 0L;
		fillPhase = 1;
		phaseAt = Environment.TickCount64;
		Audit("收到交易请求：to=" + ((to.Length > 0) ? to : "(空)") + " → " + ((toName.Length > 0) ? toName : "(等窗口)") + "，物品 " + items.Count + " 种");
		Flog("交易请求 → " + ((toName.Length > 0) ? toName : "(等窗口)") + "，物品 " + items.Count + " 种");
		if (toName.Length > 0)
		{
			IGameObject obj = await RunOnFramework(() => FindPlayerByName(toName)).ConfigureAwait(continueOnCapturedContext: false);
			if (obj != null)
			{
				string text = await RunOnFramework(delegate
				{
					IGameObject target = TargetManager.Target;
					string text2;
					if (target != null)
					{
						text2 = target.Name.TextValue;
						if (text2 == null)
						{
							return string.Empty;
						}
					}
					else
					{
						text2 = string.Empty;
					}
					return text2;
				}).ConfigureAwait(continueOnCapturedContext: false);
				Audit("选中买家「" + toName + "」→ SetHardTarget（原目标=" + (string.IsNullOrEmpty(text) ? "(无)" : ("「" + text + "」")) + "）");
				uint num4 = await RunOnFramework(() => SendTradeRequestTo(obj)).ConfigureAwait(continueOnCapturedContext: false);
				if (num4 != 0)
				{
					Audit("已发起交易：SendTradeRequest(entityID=" + num4 + ")（目标「" + toName + "」）");
					Flog("已选中「" + toName + "」→ SendTradeRequest");
				}
				else
				{
					await Task.Delay(150).ConfigureAwait(continueOnCapturedContext: false);
					await RunOnFramework(() => SendToChatBox("/trade")).ConfigureAwait(continueOnCapturedContext: false);
					Audit("SendTradeRequest 不可用，退回发 /trade（目标「" + toName + "」）");
					Flog("已选中「" + toName + "」并发起 /trade");
				}
			}
			else
			{
				Audit("场景里没有找到「" + toName + "」，改发 /trade 名字");
				Flog("场景里找不到玩家「" + toName + "」，仍尝试 /trade 名字");
				await RunOnFramework(() => SendToChatBox("/trade " + toName)).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		else
		{
			Audit("交易：未指定买家，等你手动打开交易窗口");
			Flog("没有选中目标，等你手动打开交易窗口");
		}
		return new
		{
			ok = true,
			stage = "opening",
			to = pendingTrade.To,
			items = items.Count
		};
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		try
		{
			RefreshAreaCache();
			DriveFill();
		}
		catch (Exception ex)
		{
			Log.Debug(ex, "INPA Bridge: 放物状态机异常", Array.Empty<object>());
		}
	}

	private unsafe void DriveFill()
	{
		if (gilPhase != 0)
		{
			DriveGil();
		}
		else
		{
			if (fillPhase == 0)
			{
				return;
			}
			long tickCount = Environment.TickCount64;
			if (fillPhase == 1)
			{
				if (!IsTradeOpen())
				{
					if (tickCount - phaseAt > 40000)
					{
						Flog("超时：交易窗口没打开");
						fillPhase = 0;
						pendingTrade = null;
					}
					return;
				}
				BuildFillQueue();
				fillIndex = 0;
				Audit("交易窗口已打开");
				if (pendingTrade != null && pendingTrade.Gil > 0)
				{
					Audit("需付金币=" + pendingTrade.Gil + "；交易窗口结构：" + DumpTradeNodes());
				}
				if (fillQueue == null || fillQueue.Count == 0)
				{
					fillPhase = 0;
					if (pendingTrade != null && pendingTrade.Gil > 0)
					{
						gilWant = pendingTrade.Gil;
						gilAt = tickCount;
						gilPhase = 1;
						Flog("没有要放入的物品 → 自动填金币 " + gilWant);
					}
					else
					{
						Flog("没有可放入的物品，结束");
						pendingTrade = null;
					}
				}
				else
				{
					Audit("开始放入 " + fillQueue.Count + " 个物品位");
					Flog("交易窗口已打开，开始放入 " + fillQueue.Count + " 个物品位");
					fillPhase = 2;
					phaseAt = tickCount;
					menuOpened = false;
				}
			}
			else if (fillPhase == 2)
			{
				if (!IsTradeOpen())
				{
					Flog("交易窗口被关闭，中止");
					fillPhase = 0;
					pendingTrade = null;
					return;
				}
				if (fillQueue == null || fillIndex >= fillQueue.Count)
				{
					if (fillQueue != null && fillQueue.Count > 0)
					{
						Audit("全部放入完成");
						Flog("全部放入完成");
						PushEvent("filled", "", 0L, "信物已全部放入");
					}
					if (pendingTrade != null && pendingTrade.Gil > 0)
					{
						gilWant = pendingTrade.Gil;
						gilAt = tickCount;
						gilPhase = 1;
						Audit("信物放完 → 自动填金币 " + gilWant);
						Flog("信物放完 → 自动填金币 " + gilWant);
					}
					else
					{
						fillPhase = 0;
						pendingTrade = null;
					}
					return;
				}
				FillSlot fillSlot = fillQueue[fillIndex];
				if (!menuOpened)
				{
					if (tickCount < fillReadyAt) return;
					/* 上一个物品的右键菜单和数量框完全关闭后再开下一项。
					   否则会复用旧菜单，表现为第二个绑定物品没有被放入。 */
					if (FindAddonVisible(new string[1] { "ContextMenu" }) != null) return;
					AgentInventoryContext* ptr = AgentInventoryContext.Instance();
					if (ptr != null)
					{
						((AgentInventoryContext*)ptr)->OpenForItemSlot(fillSlot.Type, fillSlot.Slot, 0, 0u);
						menuOpened = true;
						phaseAt = tickCount;
						Flog("打开物品菜单 slot=" + fillSlot.Slot + " want=" + fillSlot.Want);
					}
					return;
				}
				AtkUnitBase* ptr2 = FindAddonVisible(new string[1] { "ContextMenu" });
				if (ptr2 != null)
				{
					AgentInventoryContext* ptr3 = AgentInventoryContext.Instance();
					if (ptr3 != null)
					{
						int num = FindContextEntry(ptr3, "交易");
						if (num < 0)
						{
							num = FindContextEntry(ptr3, "Trade");
						}
						if (num >= 0)
						{
							FireContextEntry(ptr2, num);
							Flog("已点击菜单项 idx=" + num);
							fillPhase = 3;
							phaseAt = tickCount;
							return;
						}
					}
				}
				if (tickCount - phaseAt > 3000)
				{
					Flog("右键菜单里没找到「交易」项");
					fillPhase = 0;
					pendingTrade = null;
				}
			}
			else if (fillPhase == 3)
			{
				AtkUnitBase* ptr4 = FindAddonVisible(new string[1] { "InputNumeric" });
				if (ptr4 != null)
				{
					TryConfirmQuantity(ptr4, fillQueue[fillIndex].Want);
					menuOpened = false;
					fillPhase = 4;
					phaseAt = tickCount;
				}
				else if (tickCount - phaseAt > 1500)
				{
					Flog("未弹出数量框（可能整堆放入），继续下一个");
					menuOpened = false;
					fillPhase = 4;
					phaseAt = tickCount;
				}
			}
			else if (fillPhase == 4)
			{
				bool inputOpen = FindAddonVisible(new string[1] { "InputNumeric" }) != null;
				bool menuOpen = FindAddonVisible(new string[1] { "ContextMenu" }) != null;
				if (inputOpen || menuOpen)
				{
					if (tickCount - phaseAt > 4000)
					{
						Flog("等待上一个物品菜单关闭超时，中止本次放物");
						fillPhase = 0;
						pendingTrade = null;
					}
					return;
				}
				FillSlot finished = fillQueue[fillIndex];
				fillIndex++;
				Flog("物品位完成 slot=" + finished.Slot + "，准备下一项 " + fillIndex + "/" + fillQueue.Count);
				fillReadyAt = tickCount + 120L;
				fillPhase = 2;
				phaseAt = tickCount;
			}
		}
	}

	private unsafe static void TryConfirmQuantity(AtkUnitBase* addon, int want)
	{
		try
		{
			AtkValue* ptr = (AtkValue*)Marshal.AllocHGlobal(2 * Unsafe.SizeOf<AtkValue>());
			try
			{
				Unsafe.Write(&((AtkValue*)ptr)->Type, (FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType)3);
				((AtkValue*)ptr)->Int = want;
				Unsafe.Write(&((AtkValue*)((byte*)ptr + Unsafe.SizeOf<AtkValue>()))->Type, (FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType)2);
				((AtkValue*)((byte*)ptr + Unsafe.SizeOf<AtkValue>()))->Byte = 1;
				((AtkUnitBase*)addon)->FireCallback(2u, ptr, false);
			}
			finally
			{
				Marshal.FreeHGlobal((nint)ptr);
			}
			Flog("数量框 → " + want);
		}
		catch (Exception ex)
		{
			Flog("数量框处理失败: " + ex.Message);
		}
	}

	private unsafe static void BuildFillQueue()
	{
		fillQueue = new List<FillSlot>();
		InventoryManager* ptr = InventoryManager.Instance();
		if (ptr == null || pendingTrade == null)
		{
			return;
		}
		InventoryType[] array = new InventoryType[4];
		array[0] = InventoryType.Inventory1; array[1] = InventoryType.Inventory2; array[2] = InventoryType.Inventory3; array[3] = InventoryType.Inventory4;
		InventoryType[] array2 = (InventoryType[])(object)array;
		foreach (var item2 in pendingTrade.Items)
		{
			uint item = item2.Id;
			int num = item2.Qty;
			InventoryType[] array3 = array2;
			foreach (InventoryType val in array3)
			{
				if (num <= 0)
				{
					break;
				}
				InventoryContainer* inventoryContainer = ((InventoryManager*)ptr)->GetInventoryContainer(val);
				if (inventoryContainer == null)
				{
					continue;
				}
				for (int j = 0; j < ((InventoryContainer*)inventoryContainer)->Size; j++)
				{
					if (num <= 0)
					{
						break;
					}
					InventoryItem* inventorySlot = ((InventoryContainer*)inventoryContainer)->GetInventorySlot(j);
					if (inventorySlot != null && ((InventoryItem*)inventorySlot)->ItemId == item)
					{
						int quantity = ((InventoryItem*)inventorySlot)->Quantity;
						if (quantity > 0)
						{
							int num2 = Math.Min(quantity, num);
							fillQueue.Add(new FillSlot
							{
								Type = val,
								Slot = j,
								Want = num2
							});
							num -= num2;
						}
					}
				}
			}
			if (num > 0)
			{
				Flog("物品 id=" + item + " 数量不足，还差 " + num);
			}
		}
	}

	private unsafe static uint SendTradeRequestTo(IGameObject obj)
	{
		TargetManager.Target = obj;
		InventoryManager* ptr = InventoryManager.Instance();
		if (ptr == null)
		{
			return 0u;
		}
		((InventoryManager*)ptr)->SendTradeRequest(obj.EntityId);
		return obj.EntityId;
	}

	private static IGameObject? FindPlayerByName(string name)
	{
		try
		{
			foreach (IGameObject item in (IEnumerable<IGameObject>)ObjectTable)
			{
				IPlayerCharacter val = (IPlayerCharacter)(object)((item is IPlayerCharacter) ? item : null);
				if (val != null && string.Equals(((IGameObject)val).Name.TextValue ?? string.Empty, name, StringComparison.Ordinal))
				{
					return item;
				}
			}
		}
		catch (Exception ex)
		{
			Log.Debug(ex, "INPA Bridge: 查找玩家失败", Array.Empty<object>());
		}
		return null;
	}

	/* ---------------------------------------------------------------- 阶段0·区域限制
	   只有「房屋场景里的实体」发来的密聊才记工单：
	     ① 自己得站在房屋场景（住宅区屋外 13 / 房屋内部 14）——否则在大厅里谁路过说句话都成一张工单；
	     ② 发密聊的人得是当前场景里的实体（对象表里找得到这个 PC）。
	   每个庭院 / 房屋内部都是独立实例，人在屋外或别的区服密聊过来时对象表里没有他，自然就挡掉了。 */
	private static uint territoryUseId;
	private static uint territoryUseValue = uint.MaxValue;

	/* 缓存给 HTTP 线程看的两份快照（那些值只在框架线程上算，别在别的线程碰游戏数据） */
	private static uint areaUse = uint.MaxValue;   // 当前区域的 TerritoryIntendedUse
	private static bool areaHousing;               // 当前在不在房屋场景
	private static uint areaTerritory = uint.MaxValue;

	/// <summary>框架线程上每帧问一次；换区了才重新读表，平时只是一次数值比较。</summary>
	private static void RefreshAreaCache()
	{
		try
		{
			uint territory = ClientState.TerritoryType;
			if (territory == areaTerritory)
			{
				return;
			}
			areaTerritory = territory;
			areaUse = TerritoryIntendedUseNow();
			areaHousing = (areaUse == 13u || areaUse == 14u);
		}
		catch (Exception ex)
		{
			Log.Debug(ex, "INPA Bridge: 刷新区域缓存失败", Array.Empty<object>());
		}
	}

	/// <summary>当前区域的 TerritoryIntendedUse（读不到返回 uint.MaxValue）。按区域缓存，不会每次真读表。</summary>
	private static uint TerritoryIntendedUseNow()
	{
		try
		{
			uint territory = ClientState.TerritoryType;
			if (territory == territoryUseId && territoryUseValue != uint.MaxValue)
			{
				return territoryUseValue;
			}
			ExcelSheet<TerritoryType> sheet = DataManager.GetExcelSheet<TerritoryType>((ClientLanguage?)null, (string)null);
			if (sheet == null)
			{
				return uint.MaxValue;
			}
			uint use = sheet.GetRow(territory).TerritoryIntendedUse.RowId;
			territoryUseId = territory;
			territoryUseValue = use;
			return use;
		}
		catch (Exception ex)
		{
			Log.Debug(ex, "INPA Bridge: 读区域类型失败", Array.Empty<object>());
			return uint.MaxValue;
		}
	}

	private static bool InHousingTerritory()
	{
		return areaHousing;
	}

	/// <summary>比名字用：去掉 @区服、空格（半角/全角/不换行）与大小写差别。</summary>
	private static string NormName(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "";
		}
		StringBuilder stringBuilder = new StringBuilder(s.Length);
		foreach (char c in s)
		{
			if (c != ' ' && c != '\u3000' && c != '\u00a0' && c != '\t')
			{
				stringBuilder.Append(c);
			}
		}
		return stringBuilder.ToString().ToLowerInvariant();
	}

	private static string BareName(string from)
	{
		string text = (from ?? string.Empty).Trim();
		int num = text.IndexOf('@');
		if (num > 0)
		{
			text = text.Substring(0, num).Trim();
		}
		return text;
	}

	/// <summary>当前场景里所有 PC 的名字（对象表）。诊断和过滤都用它。</summary>
	private static List<string> ScenePlayerNames()
	{
		List<string> list = new List<string>();
		try
		{
			foreach (IGameObject item in (IEnumerable<IGameObject>)ObjectTable)
			{
				IPlayerCharacter val = (IPlayerCharacter)(object)((item is IPlayerCharacter) ? item : null);
				if (val != null)
				{
					list.Add(((IGameObject)val).Name.TextValue ?? "");
				}
			}
		}
		catch (Exception ex)
		{
			Log.Debug(ex, "INPA Bridge: 读场景玩家失败", Array.Empty<object>());
		}
		return list;
	}

	/// <summary>发密聊的人是不是当前场景里的实体（名字去掉 @区服 与空格后在对象表里找得到）。</summary>
	private static bool SenderInScene(string from)
	{
		return MatchScenePlayer(from) != null;
	}

	/// <summary>在场景里找这条密聊的发信人，返回对象表里那个实体（找不到返回 null）。
	/// 密聊的「发信人」串不一定等于角色名：
	///   · 好友 → 名字前面多一个游戏图标（例如 ★）；
	///   · 跨服 → 「角色名 + 一个游戏符号 + 区服名」连成一串（那个符号不是 @，所以不能按 @ 切）。
	/// 所以按「一模一样 → 前缀 → 后缀 → 包含」四层比对，拿到对象表里的真名后再说。 */
	private static IGameObject? MatchScenePlayer(string from)
	{
		string text = NormName(BareName(from));
		if (text.Length == 0)
		{
			return null;
		}
		try
		{
			foreach (IGameObject item in (IEnumerable<IGameObject>)ObjectTable)
			{
				IPlayerCharacter val = (IPlayerCharacter)(object)((item is IPlayerCharacter) ? item : null);
				if (val == null)
				{
					continue;
				}
				string text2 = NormName(((IGameObject)val).Name.TextValue ?? "");
				if (text2.Length == 0)
				{
					continue;
				}
				if (text2 == text || text.StartsWith(text2, StringComparison.Ordinal) || text.EndsWith(text2, StringComparison.Ordinal) || (text2.Length >= 2 && text.Contains(text2, StringComparison.Ordinal)))
				{
					return item;
				}
			}
		}
		catch (Exception ex)
		{
			Log.Debug(ex, "INPA Bridge: 比对场景玩家失败", Array.Empty<object>());
		}
		return null;
	}

	/// <summary>把实体的名字写成游戏认的写法：名字@区服。
	/// 跨服客人必须带区服才能密聊（否则游戏会回「该命令需要在名字中指定服务器名」），
	/// 同服的带 @区服 也一样能发，所以统一都带上。取不到就退回密聊里那串原文。</summary>
	private static string CanonicalName(IGameObject obj, string fallback)
	{
		try
		{
			string text = ((IGameObject)obj).Name.TextValue ?? "";
			string text2 = WorldNameOf(obj);
			if (text.Length > 0 && text2.Length > 0)
			{
				return text + "@" + text2;
			}
			if (text.Length > 0)
			{
				return text;
			}
		}
		catch (Exception ex)
		{
			Log.Debug(ex, "INPA Bridge: 拼角色名失败", Array.Empty<object>());
		}
		return fallback;
	}

	/// <summary>诊断：现在场景里都有谁（/scene）。排查「密聊怎么没进工单」时看这里。
	/// 带 ?name=发信人串 时，顺便用真代码跑一遍判定，回答「这条密聊算不算本场景的实体」。</summary>
	private static object HandleScene(string probe)
	{
		IPlayerCharacter localPlayer = null;
		try
		{
			localPlayer = ObjectTable.LocalPlayer;
		}
		catch (Exception ex)
		{
			Log.Debug(ex, "INPA Bridge: 读本机角色失败", Array.Empty<object>());
		}
		string text = ((localPlayer == null) ? "" : (((IGameObject)localPlayer).Name.TextValue ?? ""));
		Vector3 vector = ((localPlayer == null) ? default(Vector3) : ((IGameObject)localPlayer).Position);
		List<object> list = new List<object>();
		try
		{
			foreach (IGameObject item in (IEnumerable<IGameObject>)ObjectTable)
			{
				IPlayerCharacter val = (IPlayerCharacter)(object)((item is IPlayerCharacter) ? item : null);
				if (val != null)
				{
					float dist = (item.Position - vector).Length();
					list.Add(new
					{
						name = (((IGameObject)val).Name.TextValue ?? ""),
						world = WorldNameOf(item),
						dist = Math.Round(dist, 1)
					});
				}
			}
		}
		catch (Exception ex2)
		{
			Log.Debug(ex2, "INPA Bridge: 读场景玩家失败", Array.Empty<object>());
		}
		bool? flag = null;
		string text2 = "";
		if (!string.IsNullOrWhiteSpace(probe))
		{
			IGameObject obj = MatchScenePlayer(probe);
			flag = obj != null;
			text2 = ((obj == null) ? "" : CanonicalName(obj, ""));
		}
		return new
		{
			ok = true,
			area = areaUse,
			housing = areaHousing,
			local = text,
			count = list.Count,
			probe = (probe ?? ""),
			match = flag,
			matched = text2,
			players = list.ToArray()
		};
	}

			// -------------------------------------------------------------- /inpa 命令
	// 用来替代 Browsingway 那几个宏：显示/隐藏游戏内窗口、锁定/解锁窗口位置。
	private static string? overlayNameCache;
	
	/// <summary>Browsingway 覆盖层名。它的 /bw 命令认的是小写形式，所以这里直接读它的配置文件，
	/// 找出「地址指向本插件端口」的那个覆盖层，省得写死一个名字（改名了也不怕）。</summary>
	private static string BwOverlayName()
	{
		if (overlayNameCache != null) return overlayNameCache;
		var name = "inpa前台帮手";
		try
		{
			foreach (var cfg in BrowsingwayConfigs())
			{
				if (cfg == null || !File.Exists(cfg)) continue;
				using var doc = JsonDocument.Parse(File.ReadAllText(cfg, Encoding.UTF8));
				if (!doc.RootElement.TryGetProperty("Inlays", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
				string? hit = null, first = null;
				foreach (var inlay in arr.EnumerateArray())
				{
					if (inlay.ValueKind != JsonValueKind.Object) continue;
					if (!inlay.TryGetProperty("Name", out var nEl)) continue;
					var nm = nEl.GetString() ?? "";
					if (nm.Length == 0) continue;
					if (first == null) first = nm;
					if (inlay.TryGetProperty("Url", out var uEl) && (uEl.GetString() ?? "").Contains("18500")) { hit = nm; break; }
				}
				var pick = hit ?? first;
				if (pick != null) { name = Regex.Replace(pick, "\\s+", "").ToLower(); break; }
			}
		}
		catch (Exception e)
		{
			Log.Debug(e, "INPA: 读 Browsingway 配置失败，用默认覆盖层名");
		}
		overlayNameCache = name;
		return name;
	}
	
	private static IEnumerable<string> BrowsingwayConfigs()
	{
		var list = new List<string>();
		try
		{
			var cd = PluginInterface.ConfigDirectory;
			if (cd != null && cd.Parent != null) list.Add(Path.Combine(cd.Parent.FullName, "Browsingway.json"));
		}
		catch { }
		try
		{
			list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "Browsingway.json"));
		}
		catch { }
		return list;
	}
	
	// ---- 输入框专用字体：微软雅黑，字号跟页面在游戏里的实际大小一致 ----
	private static IFontHandle? fieldFont;
	private static bool fieldFontFailed;
	private static float fieldFontPx;
	private static bool cfgOpen;
	
	private static void OnOpenConfigUi() { cfgOpen = true; }
	
	/// <summary>
	/// 按需要的像素尺寸加载 / 复用微软雅黑。
	/// 不能拿 14px 的字形去放大（那样又糊又胖），要几号字就用几号尺寸建一份；
	/// 只有尺寸变了（改了校准里的缩放）才重建，避免每帧折腾字体图集。
	/// </summary>
	private static IFontHandle? EnsureFieldFont(float px)
	{
		px = Math.Clamp(px, 3f, 48f);
		/* 尺寸按整数像素比：字号可以调得很小（滑块到 0.1），只有"整数字号变了"才重建 */
		if (fieldFont != null && Math.Abs(Math.Round(fieldFontPx) - Math.Round(px)) < 1f) return fieldFont;
		if (fieldFont == null && fieldFontFailed) return null;
		/* 正在拖校准滑块时先不重建（重建字体图集比较费），松手后再一次到位 */
		if (fieldFont != null && ImGui.IsAnyItemActive()) return fieldFont;
		try
		{
			try { fieldFont?.Dispose(); } catch { }
			fieldFont = null;
			var path = @"C:\Windows\Fonts\msyh.ttc";
			if (!File.Exists(path)) { fieldFontFailed = true; return null; }
			fieldFont = PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
			{
				var cfg = new SafeFontConfig { SizePx = px };
				/* 覆盖：基本拉丁 + 假名 + 全角 + CJK 常用汉字（不然中文会显示成问号） */
				cfg.GlyphRanges = new ushort[] { 0x0020, 0x00FF, 0x2000, 0x206F, 0x3000, 0x30FF, 0x31F0, 0x31FF, 0xFF00, 0xFFEF, 0x4E00, 0x9FAF, 0 };
				var fp = tk.AddFontFromFile(path, cfg);
				/* 关键：让位图就按我们请求的像素尺寸光栅化（SkipHandling = 不做任何额外缩放）。
				   UI 缩放 150% 时 ImGui 绘制阶段还会乘一次全局缩放，那一份由窗口自己除掉
				   （见 DrawFieldInput 里的 SetWindowFontScale(1/GlobalScale)），这样
				   位图尺寸 = 绘制尺寸，1:1 采样，字才是清晰的。 */
				tk.SetFontScaleMode(fp, Dalamud.Interface.ManagedFontAtlas.FontScaleMode.SkipHandling);
			}));
			fieldFontPx = px;
			Log.Information("INPA: 输入框字体按 {0:0.#}px 重建（ImGui 全局缩放 {1:0.###}）", px, Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
			return fieldFont;
		}
		catch (Exception e)
		{
			fieldFont = null;
			fieldFontFailed = true;
			Log.Warning(e, "INPA: 加载微软雅黑失败，回退默认字体");
			return null;
		}
	}
	
	private static int cfgTab;           // 0=命令 1=输入框设置 2=网页连接
	private static int cfgWantTab = -1;  // 打开设置时想直接跳到哪一页（下一帧生效）

	/// <summary>打开设置窗口并直接切到某一页（0 命令 / 1 输入框设置 / 2 网页连接）。</summary>
	private static void OpenConfigTab(int tab) { cfgOpen = true; cfgWantTab = tab; }

	private static void DrawConfigWindow()
	{
		/* 设置窗口关掉时，必须把 calOpen 归零：
		   calOpen 是「输入框覆盖层不抢键盘焦点」的开关（对齐校准用）。
		   以前它靠独立窗口的 ref calOpen 自动归零，改成标签页后就没有这个时机了 ——
		   若一直留着 true，覆盖层会显示但永远拿不到键盘焦点（单击后打不了字）。 */
		if (!cfgOpen) { calOpen = false; return; }
		int want = cfgWantTab;      // 先取走（本帧用它决定选中哪个标签页）
		cfgWantTab = -1;
		try
		{
			ImGui.SetNextWindowSize(new Vector2(680f, 460f), ImGuiCond.FirstUseEver);
			bool open = ImGui.Begin("INPA Bridge 设置###inpaCfgWin", ref cfgOpen);
			if (open)
			{
				if (ImGui.BeginTabBar("###inpaTabs"))
				{
					if (ImGui.BeginTabItem("命令", (want == 0) ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
					{
						cfgTab = 0;
						DrawCmdTab();
						ImGui.EndTabItem();
					}
					if (ImGui.BeginTabItem("输入框设置", (want == 1) ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
					{
						cfgTab = 1;
						DrawCalTab();
						ImGui.EndTabItem();
					}
					if (ImGui.BeginTabItem("网页连接", (want == 2) ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
					{
						cfgTab = 2;
						DrawWebTab();
						ImGui.EndTabItem();
					}
					ImGui.EndTabBar();
				}
			}
			calOpen = open && (cfgTab == 1);   // 只有「输入框设置」页真的开着时，覆盖层才不抢焦点 / 不自动关闭
			ImGui.End();
		}
		catch (Exception e) { Log.Debug(e, "INPA: 设置窗口绘制失败"); }
	}

	private static void DrawCmdTab()
	{
		ImGui.TextColored(new Vector4(0.85f, 0.72f, 0.35f, 1f), "常用命令（可以做成游戏内宏，各占一行即可）");
		ImGui.Separator();
		ImGui.Text("/inpa                显示 / 隐藏游戏内网页窗口");
		ImGui.Text("/inpa lock           切换窗口位置锁定（锁定后页面里才能拖动、选字）");
		ImGui.Text("/inpa reload         刷新网页");
		ImGui.Text("/inpa cal            打开本窗口的「输入框设置」页");
		ImGui.Separator();
		ImGui.Text("桥接地址：http://127.0.0.1:18500/app");
	}


	private static void DrawWebTab()
	{
		try
		{
			var profiles = new string[3] { InpaWebProfile, PrincetonWebProfile, GenericWebProfile };
			ImGui.TextColored(new Vector4(0.85f, 0.72f, 0.35f, 1f), "三版网页 · 本机档案");
			ImGui.TextWrapped("托管网页通过本插件保存数据、读取历史，并使用游戏联动。三版各有独立目录和独立口令。");
			ImGui.Text("选择网页档案：");
			for (int i = 0; i < profiles.Length; i++)
			{
				if (i > 0) ImGui.SameLine();
				if (ImGui.RadioButton(WebProfileLabel(profiles[i]) + "###webProfile" + i, webProfileIndex == i))
				{
					webProfileIndex = i;
					webRootLoaded = false;
					webRootHint = "";
				}
			}
			var profile = profiles[Math.Clamp(webProfileIndex, 0, profiles.Length - 1)];
			ImGui.Separator();
			if (!webRootLoaded)
			{
				webRootLoaded = true;
				webRootInput = WebDataRoot(profile);
			}
			ImGui.Text(WebProfileLabel(profile) + "网页数据目录：");
			ImGui.SetNextItemWidth(-260f);
			ImGui.InputText("###inpaWebRoot" + profile, ref webRootInput, 1024);
			ImGui.SameLine();
			if (ImGui.Button("应用目录", new Vector2(105f, 0f)))
			{
				try
				{
					webRootInput = SaveWebRoot(webRootInput, profile);
					tokenCache = null;
					webRootHint = "已保存：" + webRootInput;
				}
				catch (Exception e) { webRootHint = "保存失败：" + e.Message; }
			}
			ImGui.SameLine();
			if (ImGui.Button("恢复默认", new Vector2(105f, 0f)))
			{
				try
				{
					var configFile = WebRootConfigFile(profile);
					if (File.Exists(configFile)) File.Delete(configFile);
					tokenCache = null;
					webRootInput = DefaultWebRoot(profile);
					webRootHint = "已恢复默认目录：" + webRootInput;
				}
				catch (Exception e) { webRootHint = "恢复失败：" + e.Message; }
			}
			if (!string.IsNullOrEmpty(webRootHint)) ImGui.TextWrapped(webRootHint);

			var dataRoot = Path.Combine(WebDataRoot(profile), "data");
			ImGui.TextWrapped("数据文件：" + Path.Combine(dataRoot, "inpa-data.json"));
			ImGui.TextWrapped("默认存档：" + Path.Combine(dataRoot, "sessions"));

			ImGui.Spacing();
			ImGui.Separator();
			var token = AccessToken(profile);
			ImGui.Text(WebProfileLabel(profile) + "网页连接口令：");
			ImGui.SetNextItemWidth(-140f);
			ImGui.InputText("###inpaWebToken" + profile, ref token, 128, ImGuiInputTextFlags.ReadOnly);
			ImGui.SameLine();
			if (ImGui.Button("复制口令", new Vector2(120f, 0f)))
			{
				try { ImGui.SetClipboardText(AccessToken(profile)); webRootHint = "网页连接口令已复制"; }
				catch (Exception e) { webRootHint = "复制失败：" + e.Message; }
			}
			ImGui.TextWrapped("把插件地址 http://127.0.0.1:18500 和当前档案的口令填进对应网页的「连接本机插件」面板。");
		}
		catch (Exception e) { Log.Debug(e, "INPA: 网页连接设置绘制失败"); }
	}

		// ---- 输入框覆盖层对齐参数（CSS 像素与游戏 UI 像素比例未知，标定一次即可） ----
	private static float fieldScale = 1f, fieldDx, fieldDy, fieldRadius = 4f, fieldFontScale = 1f;
	private static bool fieldAutoScale = true;
	/* 覆盖层配色（RGBA 0..1）。默认和 INPA 页面 CSS 一致。 */
	private static Vector4 fieldBg = new Vector4(0.031f, 0.031f, 0.031f, 1f);
	private static Vector4 fieldBorder = new Vector4(0.231f, 0.239f, 0.271f, 1f);
	private static Vector4 fieldText = new Vector4(0.929f, 0.929f, 0.929f, 1f);
	private static Vector4 fieldAccent = new Vector4(1f, 0.98f, 0f, 1f);
	private static Vector4 fieldFocus = new Vector4(1f, 0.98f, 0f, 1f);
	private static bool fieldPageTheme;
	private static Vector4 fieldPageBg = new Vector4(0.031f, 0.031f, 0.031f, 1f);
	private static Vector4 fieldPageBorder = new Vector4(0.231f, 0.239f, 0.271f, 1f);
	private static Vector4 fieldPageText = new Vector4(0.929f, 0.929f, 0.929f, 1f);
	private static Vector4 fieldPageAccent = new Vector4(1f, 0.98f, 0f, 1f);
	private static Vector4 fieldPageFocus = new Vector4(1f, 0.98f, 0f, 1f);
	private static float fieldPageRadius = 4f;
	private static bool fieldFontWarm;
	private static int fieldLayoutMode;                 // 0=自动识别，1=黑黄，2=蓝白，3=橙白，4=霓虹青
	private static string fieldDetectedProfile = "";
	private static bool fieldLayoutLoaded;
	private static string fieldPh = "";
	private static bool calOpen;
	private static bool alignLoaded;
	private static long fieldSeq;   // 页面每次切换输入框都会带递增序号，旧序号的消息直接丢弃（避免乱序把新框关掉）
	private static string fieldSession = "";   // 页面每次刷新换一个：换了就说明页面重启了，序号要重新计
	
	private static string? FieldAlignFile()
	{
		var dir = PluginDirectory();
		return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "field-align.txt");
	}

	private static string? FieldThemeFile()
	{
		var dir = PluginDirectory();
		return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "field-theme.txt");
	}

	private static string? FieldLayoutFile()
	{
		var dir = PluginDirectory();
		return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "field-layout.txt");
	}

	private static bool IsCyberCyanAccent(Vector4 accent)
	{
		return accent.X < 0.3f && accent.Y > 0.75f && accent.Z > 0.85f;
	}

	private static int FieldLayoutModeForProfile(string? profile, Vector4 accent)
	{
		return FieldThemePolicy.LayoutModeForProfile(profile, IsCyberCyanAccent(accent));
	}

	private static string FieldLayoutName(int mode)
	{
		if (mode == 1) return "黑黄";
		if (mode == 2) return "蓝白";
		if (mode == 3) return "橙白";
		if (mode == 4) return "霓虹青";
		return "自动识别";
	}

	private static string FieldLayoutProfileText(string profile)
	{
		var mode = FieldLayoutModeForProfile(profile, fieldPageAccent);
		return mode == 0 ? "等待网页连接" : (FieldLayoutName(mode) + "（" + WebProfileLabel(NormalizeWebProfile(profile)) + "）");
	}

	private static void LoadFieldLayoutMode()
	{
		if (fieldLayoutLoaded) return;
		fieldLayoutLoaded = true;
		try
		{
			var file = FieldLayoutFile();
			if (file == null || !File.Exists(file)) return;
			var value = File.ReadAllText(file, Encoding.UTF8).Trim().ToLowerInvariant();
			if (value == "black-yellow" || value == "1") fieldLayoutMode = 1;
			else if (value == "blue-white" || value == "2") fieldLayoutMode = 2;
			else if (value == "orange-white" || value == "3") fieldLayoutMode = 3;
			else if (value == "neon-cyan" || value == "4") fieldLayoutMode = 4;
			else fieldLayoutMode = 0;
		}
		catch { fieldLayoutMode = 0; }
	}

	private static void SaveFieldLayoutMode()
	{
		try
		{
			var file = FieldLayoutFile();
			if (file == null) return;
			var value = fieldLayoutMode == 1 ? "black-yellow"
				: fieldLayoutMode == 2 ? "blue-white"
				: fieldLayoutMode == 3 ? "orange-white"
				: fieldLayoutMode == 4 ? "neon-cyan"
				: "auto";
			File.WriteAllText(file, value, new UTF8Encoding(false));
		}
		catch (Exception e) { Log.Warning(e, "INPA: 保存输入框默认版式失败"); }
	}

	private static bool TryGetFieldLayoutPreset(int mode, out Vector4 bg, out Vector4 border, out Vector4 text,
		out Vector4 accent, out Vector4 focus, out float radius)
	{
		bg = fieldBg;
		border = fieldBorder;
		text = fieldText;
		accent = fieldAccent;
		focus = fieldFocus;
		radius = fieldRadius;
		if (mode == 1)
		{
			bg = new Vector4(0.0313726f, 0.0313726f, 0.0313726f, 1f);
			border = new Vector4(0.2313725f, 0.2392157f, 0.2705882f, 1f);
			text = new Vector4(0.9294118f, 0.9294118f, 0.9294118f, 1f);
			accent = new Vector4(1f, 0.9803922f, 0f, 1f);
			focus = new Vector4(1f, 0.9803922f, 0f, 1f);
			radius = 4f;
			return true;
		}
		if (mode == 2)
		{
			bg = new Vector4(0.9686275f, 0.9843137f, 0.9921569f, 1f);
			border = new Vector4(0.827451f, 0.854902f, 0.87451f, 1f);
			text = new Vector4(0.0823529f, 0.0980392f, 0.1098039f, 1f);
			accent = new Vector4(0.4901961f, 0.9019608f, 1f, 1f);
			focus = new Vector4(0.1843137f, 0.7372549f, 0.8509804f, 1f);
			radius = 4f;
			return true;
		}
		if (mode == 3)
		{
			bg = new Vector4(1f, 0.9803922f, 0.9411765f, 1f);
			border = new Vector4(0.8627451f, 0.8470588f, 0.8f, 1f);
			text = new Vector4(0.1098039f, 0.0941176f, 0.0705882f, 1f);
			accent = new Vector4(1f, 0.7019608f, 0.1215686f, 1f);
			focus = new Vector4(0.7882353f, 0.5176471f, 0f, 1f);
			radius = 4f;
			return true;
		}
		if (mode == 4)
		{
			bg = new Vector4(0.0313726f, 0.0313726f, 0.0313726f, 1f);
			border = new Vector4(0.2313725f, 0.2392157f, 0.2705882f, 1f);
			text = new Vector4(0.9294118f, 0.9294118f, 0.9294118f, 1f);
			accent = new Vector4(0.1450980f, 0.9294118f, 1f, 1f);
			focus = new Vector4(0.1450980f, 0.9294118f, 1f, 1f);
			radius = 4f;
			return true;
		}
		return false;
	}

	private static void ApplyFieldLayoutPreset(int mode)
	{
		if (!TryGetFieldLayoutPreset(mode, out var bg, out var border, out var text,
			out var accent, out var focus, out var radius)) return;
		fieldBg = bg;
		fieldBorder = border;
		fieldText = text;
		fieldAccent = accent;
		fieldFocus = focus;
		fieldRadius = radius;
	}

	private static void SelectFieldLayout(int mode)
	{
		fieldLayoutMode = Math.Clamp(mode, 0, 4);
		if (fieldLayoutMode > 0)
		{
			ApplyFieldLayoutPreset(fieldLayoutMode);
			SaveFieldAlign();
		}
		SaveFieldLayoutMode();
	}
	
	private static void LoadFieldAlign()
	{
		if (alignLoaded)
		{
			if (fieldAutoScale) ApplyAutoFieldScale();
			return;
		}
		alignLoaded = true;
		LoadFieldLayoutMode();
		try
		{
			bool hasBg = false, hasBorder = false, hasText = false, hasAccent = false, hasFocus = false;
			var p = FieldAlignFile();
			if (p != null && File.Exists(p))
			{
				// Old calibration files keep their manual values unless auto mode
				// was explicitly enabled and saved.
				fieldAutoScale = false;
				foreach (var line in File.ReadAllLines(p))
				{
					var kv = line.Split((char)61);
					if (kv.Length != 2) continue;
					var k = kv[0].Trim();
					if (k == "autoscale")
					{
						fieldAutoScale = kv[1].Trim() == "1" ||
							kv[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
						continue;
					}
					if (k == "bg") { fieldBg = ParseColor(kv[1], fieldBg); hasBg = true; continue; }
					if (k == "border") { fieldBorder = ParseColor(kv[1], fieldBorder); hasBorder = true; continue; }
					if (k == "text") { fieldText = ParseColor(kv[1], fieldText); hasText = true; continue; }
					if (k == "accent") { fieldAccent = ParseColor(kv[1], fieldAccent); hasAccent = true; continue; }
					if (k == "focus") { fieldFocus = ParseColor(kv[1], fieldFocus); hasFocus = true; continue; }
					if (!float.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) continue;
					if (k == "scale") fieldScale = Math.Clamp(f, 0.4f, 3f);
					else if (k == "dx") fieldDx = Math.Clamp(f, -400f, 400f);
					else if (k == "dy") fieldDy = Math.Clamp(f, -400f, 400f);
					else if (k == "radius") fieldRadius = Math.Clamp(f, 0f, 24f);
					else if (k == "fontsize") fieldFontScale = Math.Clamp(f, 0.1f, 1.6f);
				}
			}

			/* 发包时随插件附带的版本默认色。只补 field-align.txt 里没有的颜色，
			   用户自己校准过的颜色仍然优先。 */
			var theme = FieldThemeFile();
			if (theme != null && File.Exists(theme))
			{
				foreach (var line in File.ReadAllLines(theme))
				{
					var kv = line.Split((char)61);
					if (kv.Length != 2) continue;
					var k = kv[0].Trim();
					if (k == "bg" && !hasBg) { fieldBg = ParseColor(kv[1], fieldBg); continue; }
					if (k == "border" && !hasBorder) { fieldBorder = ParseColor(kv[1], fieldBorder); continue; }
					if (k == "text" && !hasText) { fieldText = ParseColor(kv[1], fieldText); continue; }
					if (k == "accent" && !hasAccent) { fieldAccent = ParseColor(kv[1], fieldAccent); continue; }
					if (k == "focus" && !hasFocus) { fieldFocus = ParseColor(kv[1], fieldFocus); continue; }
					if (k == "radius" && float.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
						fieldRadius = Math.Clamp(r, 0f, 24f);
				}
			}
			if (fieldAutoScale) ApplyAutoFieldScale();
		}
		catch { }
	}

	private static void ApplyAutoFieldScale()
	{
		var io = ImGui.GetIO();
		var result = FieldScalePolicy.Compute(
			io.DisplaySize.X,
			io.DisplaySize.Y,
			Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
		fieldScale = result.BoxScale;
		fieldFontScale = result.FontScale;
	}
	
	private static void SaveFieldAlign()
	{
		try
		{
			var p = FieldAlignFile();
			if (p == null) return;
			File.WriteAllText(p, "autoscale=" + (fieldAutoScale ? "1" : "0")
				+ Environment.NewLine + "scale=" + fieldScale.ToString("0.####", CultureInfo.InvariantCulture)
				+ Environment.NewLine + "dx=" + fieldDx.ToString("0.##", CultureInfo.InvariantCulture)
				+ Environment.NewLine + "dy=" + fieldDy.ToString("0.##", CultureInfo.InvariantCulture)
				+ Environment.NewLine + "radius=" + fieldRadius.ToString("0.##", CultureInfo.InvariantCulture)
				+ Environment.NewLine + "fontsize=" + fieldFontScale.ToString("0.##", CultureInfo.InvariantCulture)
				+ Environment.NewLine + "bg=" + FmtColor(fieldBg)
				+ Environment.NewLine + "border=" + FmtColor(fieldBorder)
				+ Environment.NewLine + "text=" + FmtColor(fieldText)
				+ Environment.NewLine + "accent=" + FmtColor(fieldAccent)
				+ Environment.NewLine + "focus=" + FmtColor(fieldFocus), new UTF8Encoding(false));
		}
		catch (Exception e) { Log.Warning(e, "INPA: 保存对齐参数失败"); }
	}
	
	private static void DrawCalTab()
	{
		try
		{
			LoadFieldAlign();
			ImGui.TextColored(new Vector4(0.85f, 0.72f, 0.35f, 1f), "默认版式");
			for (var mode = 0; mode <= 4; mode++)
			{
				if (mode > 0) ImGui.SameLine();
				if (ImGui.RadioButton(FieldLayoutName(mode) + "###fieldLayout" + mode, fieldLayoutMode == mode))
					SelectFieldLayout(mode);
			}
			var detected = fieldDetectedProfile.Length > 0
				? FieldLayoutProfileText(fieldDetectedProfile)
				: "等待网页连接";
			ImGui.TextWrapped("自动识别当前：" + detected +
				(fieldLayoutMode == 0 ? "" : "；当前手动覆盖为" + FieldLayoutName(fieldLayoutMode)));
			ImGui.TextWrapped("自动模式会按当前网页版本及皮肤切换黑黄 / 蓝白 / 橙白 / 霓虹青；手动模式优先使用所选版式。");
			ImGui.Separator();
			if (ImGui.Checkbox("自动适配界面", ref fieldAutoScale))
			{
				if (fieldAutoScale) ApplyAutoFieldScale();
				SaveFieldAlign();
			}
			if (fieldAutoScale)
			{
				var io = ImGui.GetIO();
				ImGui.TextWrapped("当前显示 " + Math.Round(io.DisplaySize.X) + "×" + Math.Round(io.DisplaySize.Y)
					+ "，界面缩放 " + (Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale * 100f).ToString("0.#", CultureInfo.InvariantCulture)
					+ "% → 输入框 " + fieldScale.ToString("0.000", CultureInfo.InvariantCulture)
					+ " / 字号 " + fieldFontScale.ToString("0.00", CultureInfo.InvariantCulture));
				ImGui.TextWrapped("切换分辨率、显示器或界面缩放后会自动重新计算；需要精调时可关闭自动适配。");
			}
			ImGui.TextWrapped("先点一下页面里的输入框（会出现一个覆盖框），再拖下面的滑块让它和页面的框完全重合；颜色在下面调。改完点保存。");
			ImGui.Spacing();
			ImGui.BeginDisabled(fieldAutoScale);
			ImGui.SetNextItemWidth(300f);
			ImGui.SliderFloat("缩放", ref fieldScale, 0.5f, 2f, "%.3f");
			ImGui.EndDisabled();
			ImGui.SetNextItemWidth(300f);
			ImGui.SliderFloat("左右偏移", ref fieldDx, -150f, 150f, "%.0f");
			ImGui.SetNextItemWidth(300f);
			ImGui.SliderFloat("上下偏移", ref fieldDy, -60f, 60f, "%.0f");
			ImGui.SetNextItemWidth(300f);
			ImGui.SliderFloat("圆角", ref fieldRadius, 0f, 20f, "%.0f");
			ImGui.BeginDisabled(fieldAutoScale);
			ImGui.SetNextItemWidth(300f);
			ImGui.SliderFloat("字号（1.00 = 和页面一样大）", ref fieldFontScale, 0.1f, 1.6f, "%.2f");
			ImGui.EndDisabled();
			ImGui.Spacing();
			ImGui.TextColored(new Vector4(0.85f, 0.72f, 0.35f, 1f), "颜色");
			ImGui.SetNextItemWidth(300f);
			ImGui.ColorEdit4("背景色", ref fieldBg, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf);
			ImGui.SetNextItemWidth(300f);
			ImGui.ColorEdit4("边框色", ref fieldBorder, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf);
			ImGui.SetNextItemWidth(300f);
			ImGui.ColorEdit4("文字色", ref fieldText, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf);
			ImGui.SetNextItemWidth(300f);
			ImGui.ColorEdit4("信号导轨色", ref fieldAccent, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf);
			ImGui.SetNextItemWidth(300f);
			ImGui.ColorEdit4("聚焦边框色", ref fieldFocus, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf);
			ImGui.Spacing();
			if (ImGui.Button("保存", new Vector2(90f, 0f))) { SaveFieldAlign(); ChatGui.Print("INPA：输入框设置已保存"); }
			ImGui.SameLine();
			if (ImGui.Button("重置", new Vector2(90f, 0f)))
			{
				fieldScale = 1f; fieldDx = 0f; fieldDy = 0f; fieldRadius = 4f; fieldFontScale = 1f;
				fieldAutoScale = true;
				ApplyAutoFieldScale();
				fieldBg = new Vector4(0.031f, 0.031f, 0.031f, 1f);
				fieldBorder = new Vector4(0.231f, 0.239f, 0.271f, 1f);
				fieldText = new Vector4(0.929f, 0.929f, 0.929f, 1f);
				fieldAccent = new Vector4(1f, 0.98f, 0f, 1f);
				fieldFocus = new Vector4(1f, 0.98f, 0f, 1f);
			}
		}
		catch (Exception e) { Log.Debug(e, "INPA: 输入框设置页绘制失败"); }
	}
		// ------------------------------------------------------ 页面输入框「直连」输入法
	// 游戏内网页（CEF 无窗口模式）没有输入法，中文进不去。做法：页面点中输入框时告诉插件，
	// 插件就在你点击的位置盖一个「隐形」ImGui 输入框 —— 键盘与输入法交给 Dalamud 的 ImGui
	// （支持中文、候选词定位），打出来的字实时镜像回页面那个框，看起来就跟直接在页面里打字一样。

	private static bool fieldActive;
	private static bool fieldKeysOnly;
	private static bool fieldGuardRawEscDown;
	private static float fieldW, fieldH, fieldRx, fieldRy;
	/* 页面视口（整张网页）在游戏屏幕上的原点。定位输入框覆盖层的正确做法是
	   「视口原点 + 字段在页面里的坐标 * 缩放」，而不是「鼠标位置 - 点进框里的偏移」——
	   后者只在"用鼠标点开这个框"时成立。页面自己 focus() 打开的框（比如设置页弹窗里
	   自动聚焦的筛选框）没有点击，用鼠标算就会把框丢到屏幕别处。 */
	private static float fieldOriginX, fieldOriginY;
	private static bool fieldOriginValid;
	private static float fieldRl, fieldRt;   // 字段在页面视口里的左上角（CSS px）
	private static bool fieldHasClick;       // 这次的框是"鼠标点开的"吗
	private static float fieldCx, fieldCy;   // 点开时，鼠标点在页面视口里的坐标
	private static float fieldRequestDy;     // 页面按字段指定的纵向微调（CSS px）
	private static bool fieldMulti;          // 多行框（页面里的 textarea，比如宏内容）
	/* 聊天框那种「回车=发送、输入框不关」的框：页面在 /field 里带 keep=1 */
	private static bool fieldKeepOnEnter;
	private static long fieldEnterSeq;       // 回车发送计数：页面靠它去重（一次发送只处理一次）
	private static long fieldEscSeq;         // 按 Esc 结束输入的计数（聊天框靠它"按 Esc 关掉聊天框"）
	/* 页面每点一次鼠标就报一次点击位置（视口坐标），插件拿到后配上自己的鼠标位置，
	   就能量出"整页视口在屏幕上的原点"。量一次就够，之后页面自己 focus 的框都能摆对。 */
	private static float clickPosX, clickPosY;
	private static int clickSerial;
	private static int clickSeen;
	private static Vector2 fieldScreenPos;
	private static bool fieldHadFocus;
	private static bool fieldFocusLostLogged;   // "焦点丢失"只记一次，避免刷日志
	private static readonly bool[] fieldDigitDown = new bool[10];   // 数字键上一帧是否按下（用来记"ImGui 有没有收到"）
	/* 物理按键兜底用：数字/退格被 Dalamud 拦下又不转发时（WantTextInput=false），直接问 Windows 按键状态。
	   它跟游戏的消息处理完全无关，所以不会因为"FF14 把 1-0 当技能栏热键"而丢。 */
	[DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
	[DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
	[DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr hWnd);
	[DllImport("imm32.dll")] private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
	[DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern int ImmGetCompositionStringW(IntPtr hIMC, uint dwIndex, byte[]? lpBuf, int dwBufLen);
	private const uint GCS_COMPSTR = 0x0008;
	/* 现在是否正在用输入法组词（拼音/候选还没上屏）？
	   组词期间拼音只存在于输入法里，页面/插件看到的文本是"没变"的，
	   这时如果还去补字符/删字符，就会把组词打断 —— 表现为 zhi'm'n 这种被切开的拼音。
	   所以组词期间所有物理兜底一律让位。 */
	private static bool IsImeComposing()
	{
		try
		{
			var hwnd = GetForegroundWindow();
			if (hwnd == IntPtr.Zero) return false;
			var hImc = ImmGetContext(hwnd);
			if (hImc == IntPtr.Zero) return false;
			try { return ImmGetCompositionStringW(hImc, GCS_COMPSTR, null, 0) > 0; }
			finally { ImmReleaseContext(hwnd, hImc); }
		}
		catch { return false; }
	}
	/// <summary>读当前输入法的组词内容（诊断用：能看出 ' 是输入法给的还是我们插的）。</summary>
	private static string ImeCompositionText()
	{
		try
		{
			var hwnd = GetForegroundWindow();
			if (hwnd == IntPtr.Zero) return "";
			var hImc = ImmGetContext(hwnd);
			if (hImc == IntPtr.Zero) return "";
			try
			{
				var len = ImmGetCompositionStringW(hImc, GCS_COMPSTR, null, 0);
				if (len <= 0) return "";
				var buf = new byte[len];
				ImmGetCompositionStringW(hImc, GCS_COMPSTR, buf, len);
				return Encoding.Unicode.GetString(buf, 0, len);
			}
			finally { ImmReleaseContext(hwnd, hImc); }
		}
		catch { return ""; }
	}
	private static readonly bool[] fieldRawDigitDown = new bool[10];
	private static bool fieldRawBackDown, fieldRawDelDown;
	/* "物理兜底"攒下来的编辑动作：必须交给 ImGui 在回调里执行，
	   直接改 fieldBuf 会被 ImGui 的内部编辑状态覆盖回去（试过一次，数字看着补了其实没留住）。 */
	private static string fieldPendingText = "";
	private static int fieldPendingBack, fieldPendingDel;
	/* 物理按键"挂起 3 帧"用：等 ImGui 自己收到就不兜底，避免重复输入 */
	private static readonly int[] fieldDigitWaitFrame = new int[10];
	private static readonly bool[] fieldDigitSawImGui = new bool[10];
	private static readonly bool[] fieldDigitShifted = new bool[10];   // 按下时 Shift 是否按住（决定补 @ # % 还是 2 3 5）
	private static readonly bool[] fieldDigitSkip = new bool[10];      // 按下时 Ctrl/Alt 按住 → 不补（那是快捷键组合）
	/* 按下瞬间记下文本长度：3 帧后如果文本已经变了，说明这个键已经被输入法处理（例如中文的「（」），
	   兜底就必须让位，否则会出现「（（））」这种重复。 */
	private static readonly int[] fieldDigitBuf0 = new int[10];
	private static int fieldBackBuf0, fieldDelBuf0, fieldEnterBuf0;
	private static int fieldBackWaitFrame = -1, fieldDelWaitFrame = -1;
	private static bool fieldBackSawImGui, fieldDelSawImGui;
	private static int fieldEnterWaitFrame = -1, fieldEscWaitFrame = -1;
	private static bool fieldEnterSawImGui, fieldEscSawImGui;
	private static bool fieldRawEnterDown, fieldRawEscDown;
	private static bool fieldImeComposing;   // 输入法是否正在组词（组词期间物理兜底让位）
	/* 「文本最近被外部改过」的判据：输入法选词/上屏就在按下的那一瞬间完成，
	   等我们 3 帧后再看 composition 已经结束，只能靠"文本刚被改过"来识别。 */
	private static string fieldBufPrev = "";
	private static int fieldBufChangedFrame = -9999;
	private static bool fieldSelfEdited;     // 这次变化是我们自己补的（不算外部改动）
	private static int fieldLastFallbackLog = -9999;   // 兜底日志限流（只记第一笔，避免刷屏）
	private static string fieldBuf = string.Empty;
	private static int fieldFrames;
	private static Vector2 fieldAnchor;
	private static Vector2 fieldSize = new Vector2(320f, 26f);

	private static void SetFieldTarget(bool active, float w, float h, float rx, float ry, string? value)
	{
		/* 页面回传的值和当前正在编辑的内容不一样 → 说明页面把我们刚打的字覆盖回去了（会表现为"按键没反应"） */
		if (active && fieldActive && value != null && fieldBuf != null && value != fieldBuf)
			Audit("输入框：页面回传了 " + value.Length + " 字的值，覆盖了当前正在编辑的 " + fieldBuf.Length + " 字内容");
		fieldKeysOnly = false;
		fieldActive = active;
		fieldGuardRawEscDown = false;
		fieldW = w; fieldH = h; fieldRx = rx; fieldRy = ry;
		fieldRequestDy = 0f;
		fieldBuf = value ?? string.Empty;
		fieldFrames = 0;
		fieldHadFocus = false;
		fieldFocusLostLogged = false;
		for (var i = 0; i < fieldDigitDown.Length; i++) fieldDigitDown[i] = false;
		for (var i = 0; i < fieldRawDigitDown.Length; i++) fieldRawDigitDown[i] = false;
		fieldRawBackDown = false;
		fieldRawDelDown = false;
		fieldPendingText = "";
			fieldPendingBack = 0;
			fieldPendingDel = 0;
			fieldBufPrev = fieldBuf;
			fieldBufChangedFrame = -9999;
			fieldSelfEdited = false;
		for (var i = 0; i < fieldDigitWaitFrame.Length; i++)
		{
			fieldDigitWaitFrame[i] = -1; fieldDigitSawImGui[i] = false;
			fieldDigitShifted[i] = false; fieldDigitSkip[i] = false;
			fieldDigitBuf0[i] = 0;
		}
		fieldBackWaitFrame = -1; fieldDelWaitFrame = -1;
		fieldBackSawImGui = false; fieldDelSawImGui = false;
		fieldBackBuf0 = 0; fieldDelBuf0 = 0; fieldEnterBuf0 = 0;
		fieldEnterWaitFrame = -1; fieldEscWaitFrame = -1;
		fieldEnterSawImGui = false; fieldEscSawImGui = false;
		fieldRawEnterDown = false; fieldRawEscDown = false;
		fieldImeComposing = false;
		fieldBufPrev = fieldBuf;
		fieldBufChangedFrame = -9999;
		fieldSelfEdited = false;
	}

	private static object FieldState()
	{
		/* 带上 seq：页面靠它判断"插件报的 active=false"是不是自己刚发的那一条，
		   免得切输入框时把新开的那条误判成"插件已经结束"。 */
		/* enter：聊天框专用的"回车发送"计数（每次回车 +1），页面拿它去重后发消息。 */
		return new { ok = true, active = fieldActive || fieldKeysOnly, keysOnly = fieldKeysOnly, text = fieldBuf, seq = fieldSeq, enter = fieldEnterSeq, esc = fieldEscSeq };
	}

	private async Task<object> HandleFieldPostAsync(HttpListenerRequest req)
	{
		string body;
		using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
			body = await reader.ReadToEndAsync().ConfigureAwait(false);
		try
		{
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;
			bool active = !(root.TryGetProperty("active", out var aEl) && aEl.ValueKind == JsonValueKind.False);
			float F(string n)
			{
				if (root.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.Number)
				{
					try { return (float)e.GetDouble(); } catch { }
				}
				return 0f;
			}
			string S(string n)
			{
				if (root.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String)
					return e.GetString() ?? "";
				return "";
			}
			string value = "";
			if (root.TryGetProperty("value", out var vEl) && vEl.ValueKind == JsonValueKind.String) value = vEl.GetString() ?? "";
			fieldPh = (root.TryGetProperty("ph", out var pEl) && pEl.ValueKind == JsonValueKind.String) ? (pEl.GetString() ?? "") : "";
			long seq = 0;
			if (root.TryGetProperty("seq", out var sEl) && sEl.ValueKind == JsonValueKind.Number) { try { seq = sEl.GetInt64(); } catch { } }
			/* 页面刷新过（会话号变了）：它的序号从 1 重新数，插件这边必须跟着归零，
			   否则新页面的"开始编辑"会被当成旧消息丢掉 —— 页面已经把输入框藏起来了，
			   覆盖层却永远不开，看上去就是"输入框消失了"。 */
			string? sid = (root.TryGetProperty("sid", out var sidEl) && sidEl.ValueKind == JsonValueKind.String) ? sidEl.GetString() : null;
			if (!string.IsNullOrEmpty(sid))
			{
				if (sid != fieldSession)
				{
					if (fieldSession.Length > 0) Audit("输入框：页面刷新了，序号重新计数");
					fieldSession = sid!;
					fieldSeq = 0;
					fieldActive = false;
				}
			}
			if (seq > 0 && seq < fieldSeq) return new { ok = true, ignored = true };   // 旧消息：丢掉
			if (seq > 0) fieldSeq = seq;
			/* 页面这条消息带的是"字段在视口里的位置"，外加"是不是鼠标点开的 + 点在哪"。
			   带点击 → 插件可以用鼠标位置反推出视口原点并记住；
			   不带点击（页面自己 focus 的框）→ 沿用上一次记住的原点。 */
			fieldRl = F("rl"); fieldRt = F("rt");
			fieldMulti = F("multi") > 0.5f;   // 页面把它标成 textarea 时用多行编辑器
			fieldKeepOnEnter = F("keep") > 0.5f;   // 聊天框：回车只发送，不关输入框
			bool keysOnly = root.TryGetProperty("keys", out var keysEl) &&
				(keysEl.ValueKind == JsonValueKind.True ||
				 (keysEl.ValueKind == JsonValueKind.Number && keysEl.TryGetDouble(out var keysNum) && keysNum > 0));
			/* 页面按当前版本传入输入框视觉 token；比只靠发包目录里的主题文件更可靠，
			   同一份插件也能在 INPA / 普林斯顿 / 通用版之间即时切换配色。 */
			if (active)
			{
				/* 只有正在编辑的输入框请求才能改变自动主题档案。
				   其他页面、探活和后台轮询不能覆盖它，否则输入框会在
				   黑黄 / 橙白等版式之间反复跳色。 */
				fieldDetectedProfile = FieldThemePolicy.UpdateDetectedProfile(
					fieldDetectedProfile, ProfileName(req), editingRequest: true);
				var bg = S("fbg");
				var border = S("fborder");
				var text = S("ftx");
				var accent = S("faccent");
				var focus = S("ffocus");
				if (bg.Length > 0) fieldPageBg = ParseColor(bg, fieldPageBg);
				if (border.Length > 0) fieldPageBorder = ParseColor(border, fieldPageBorder);
				if (text.Length > 0) fieldPageText = ParseColor(text, fieldPageText);
				if (accent.Length > 0) fieldPageAccent = ParseColor(accent, fieldPageAccent);
				if (focus.Length > 0) fieldPageFocus = ParseColor(focus, fieldPageFocus);
				var radius = F("fradius");
				if (radius > 0f) fieldPageRadius = Math.Clamp(radius, 0f, 24f);
				fieldPageTheme = bg.Length > 0 || border.Length > 0 || text.Length > 0 ||
					accent.Length > 0 || focus.Length > 0 || radius > 0f;
			}
			else fieldPageTheme = false;
			bool hasClick = root.TryGetProperty("click", out var cEl) && cEl.ValueKind == JsonValueKind.True;
			if (hasClick) { fieldCx = F("cx"); fieldCy = F("cy"); }
			fieldHasClick = hasClick && active;
			if (active != fieldActive) Audit("输入框：" + (active ? "开始编辑" : "结束编辑") + "（seq=" + seq + (active ? (hasClick ? "，鼠标点开" : (fieldOriginValid ? "，页面自动聚焦，沿用上次位置" : "，页面自动聚焦，还没量过位置")) : "") + "）");
			if (active && keysOnly)
			{
				SetFieldTarget(false, 0f, 0f, 0f, 0f, null);
				fieldKeysOnly = true;
				fieldGuardRawEscDown = (GetAsyncKeyState(0x1B) & 0x8000) != 0;
				fieldFrames = 0;
			}
			else
			{
				SetFieldTarget(active, F("w"), F("h"), F("rx"), F("ry"), value);
				if (active) fieldRequestDy = F("fdy");
			}
			return new { ok = true, active = fieldActive || fieldKeysOnly, keysOnly = fieldKeysOnly, seq = fieldSeq, enter = fieldEnterSeq, esc = fieldEscSeq };
		}
		catch (Exception e)
		{
			return new { ok = false, error = e.Message };
		}
	}

	/* 页面每次按下鼠标都会报一下点击位置（视口坐标）。配合本帧的鼠标位置就能量出
	   "整页视口在屏幕上的原点"，量一次记住，之后页面自己 focus 的框也能摆到正确位置。 */
	private async Task<object> HandleClickPostAsync(HttpListenerRequest req)
	{
		try
		{
			string body;
			using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
				body = await reader.ReadToEndAsync().ConfigureAwait(false);
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;
			float G(string n)
			{
				if (root.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.Number)
				{
					try { return (float)e.GetDouble(); } catch { }
				}
				return float.NaN;
			}
			var x = G("x"); var y = G("y");
			if (float.IsNaN(x) || float.IsNaN(y)) return new { ok = false };
			clickPosX = x; clickPosY = y;
			clickSerial++;
			return new { ok = true };
		}
		catch (Exception e)
		{
			return new { ok = false, error = e.Message };
		}
	}

	/* Browsingway 的鼠标点击不一定携带 Ctrl 修饰状态；页面在游戏内需要多选时，
	   通过这个本地接口读取 Windows 物理按键，避免把 Ctrl 混入 FF14 的技能热键。 */
	private static object PhysicalModifierState()
	{
		bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
		bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
		bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
		bool meta = (GetAsyncKeyState(0x5B) & 0x8000) != 0 ||
			(GetAsyncKeyState(0x5C) & 0x8000) != 0;
		return new { ok = true, ctrl, shift, alt, meta };
	}

	/// <summary>绘制线程：把页面刚报来的那次点击换算成视口原点。</summary>
	private static void LearnFieldOrigin()
	{
		int serial = clickSerial;
		if (serial == clickSeen) return;
		clickSeen = serial;
		try
		{
			var io = ImGui.GetIO();
			LoadFieldAlign();
			fieldOriginX = io.MousePos.X - clickPosX * fieldScale;
			fieldOriginY = io.MousePos.Y - clickPosY * fieldScale;
			fieldOriginValid = true;
		}
		catch { }
	}

	/// <summary>"r,g,b,a" / "r,g,b" → 颜色（读不出来就用原值）。</summary>
	private static Vector4 ParseColor(string s, Vector4 fallback)
	{
		try
		{
			var parts = s.Split(',');
			if (parts.Length < 3) return fallback;
			var c = new float[4];
			for (var i = 0; i < parts.Length && i < 4; i++)
				if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out c[i])) return fallback;
			var a = (parts.Length >= 4) ? c[3] : 1f;
			return new Vector4(Math.Clamp(c[0], 0f, 1f), Math.Clamp(c[1], 0f, 1f), Math.Clamp(c[2], 0f, 1f), Math.Clamp(a, 0f, 1f));
		}
		catch { return fallback; }
	}

	private static string FmtColor(Vector4 c)
	{
		return c.X.ToString("0.####", CultureInfo.InvariantCulture) + "," + c.Y.ToString("0.####", CultureInfo.InvariantCulture)
			+ "," + c.Z.ToString("0.####", CultureInfo.InvariantCulture) + "," + c.W.ToString("0.####", CultureInfo.InvariantCulture);
	}

	private static void DrawOverlayHook()
	{
		LearnFieldOrigin();
		if (!fieldFontWarm)
		{
			try
			{
				LoadFieldAlign();
				var px = 14f * Math.Clamp(fieldScale, 0.5f, 3f) * fieldFontScale;
				EnsureFieldFont(px);
			}
			catch { }
			fieldFontWarm = true;
		}
		if (fieldKeysOnly) DrawFieldKeyGuard();
		DrawFieldInput();
	}

	/* ---- 输入框回调：在这里执行"物理兜底"攒下的编辑动作 ----
	   ImGui 的输入框有自己的内部编辑状态（stb_textedit），直接改 fieldBuf 会在下一帧被它覆盖回去；
	   必须在回调里通过它的 API 改，改动才算真正生效。 */
	private static unsafe int OnFieldTextCallback(scoped ref ImGuiInputTextCallbackData d)
	{
		try
		{
			if (d.EventFlag != ImGuiInputTextFlags.CallbackAlways) return 0;
			/* 没有待处理动作时，绝不碰这根输入框（否则会把用户的选中内容也删掉）。 */
			if (fieldPendingText.Length == 0 && fieldPendingBack <= 0 && fieldPendingDel <= 0) return 0;
			var did = false;
			/* 1) 有选中内容时，由"这次输入/删除"把它替换掉（和普通打字一致），只消耗一次动作 */
			if (d.SelectionEnd > d.SelectionStart)
			{
				var selStart = d.SelectionStart;
				d.DeleteChars(selStart, d.SelectionEnd - selStart);
				d.CursorPos = selStart; d.SelectionStart = d.SelectionEnd = selStart;
				if (fieldPendingBack > 0) fieldPendingBack--;          // 选中内容就是这次退格的目标
				else if (fieldPendingDel > 0) fieldPendingDel--;       // Delete 同理
				did = true;
			}
			/* 2) 补入字符（数字/符号），插在光标处；光标位置自己设定，不依赖 ImGui 的自动调整 */
			if (fieldPendingText.Length > 0)
			{
				var pos = d.CursorPos;
				foreach (var ch in fieldPendingText)
				{
					if ((ch < 32 || ch > 126) && ch != '\n' && ch != '\t') continue;   // 放行可见 ASCII + 换行/制表
					InsertAsciiByte(ref d, pos, (byte)ch);
					pos++;
				}
				d.CursorPos = pos; d.SelectionStart = d.SelectionEnd = pos;
				fieldPendingText = "";
				did = true;
			}
			/* 3) 退格 = 删光标前一个字；Delete = 删光标后一个字（按 UTF-8 字符边界，中文也能正确删） */
			while (fieldPendingBack > 0)
			{
				fieldPendingBack--;
				if (d.CursorPos <= 0) break;
				var n = PrevUtf8Len(ref d, d.CursorPos);
				var at = d.CursorPos - n;
				d.DeleteChars(at, n);
				d.CursorPos = at; d.SelectionStart = d.SelectionEnd = at;
				did = true;
			}
			fieldPendingBack = 0;
			while (fieldPendingDel > 0)
			{
				fieldPendingDel--;
				if (d.CursorPos >= d.BufTextLen) break;
				var n = NextUtf8Len(ref d, d.CursorPos);
				var at = d.CursorPos;
				d.DeleteChars(at, n);
				d.CursorPos = at; d.SelectionStart = d.SelectionEnd = at;
				did = true;
			}
			fieldPendingDel = 0;
			if (did) { d.BufDirty = 1; fieldSelfEdited = true; }   // 这是我们自己补的，不算"输入法改的"
		}
		catch { }
		return 0;
	}

	private static void DrawFieldKeyGuard()
	{
		var io = ImGui.GetIO();
		/* 模态框没有输入框时也要把下一次 Esc 留在插件这一层。窗口本身不绘制、
		   不接鼠标，只占一个屏幕外的键盘焦点；物理键兜底防止 Dalamud 漏转。 */
		io.WantCaptureKeyboard = true;
		io.WantTextInput = false;
		var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
			| ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse
			| ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMouseInputs
			| ImGuiWindowFlags.NoDocking;
		ImGui.SetNextWindowPos(new Vector2(-10000f, -10000f), ImGuiCond.Always);
		ImGui.SetNextWindowSize(new Vector2(8f, 8f), ImGuiCond.Always);
		if (fieldFrames == 0)
		{
			ImGui.SetNextWindowFocus();
			fieldGuardRawEscDown = (GetAsyncKeyState(0x1B) & 0x8000) != 0;
		}
		ImGui.Begin("##inpaModalKeyGuard", flags);
		var escImGui = ImGui.IsKeyPressed(ImGuiKey.Escape);
		var escPhys = (GetAsyncKeyState(0x1B) & 0x8000) != 0;
		var escJust = escPhys && !fieldGuardRawEscDown;
		fieldGuardRawEscDown = escPhys;
		if (escImGui || escJust)
		{
			fieldEscSeq++;
			fieldActive = false;
			fieldKeysOnly = false;
			Audit("输入框：模态框键盘守卫收到 Esc");
		}
		ImGui.End();
		fieldFrames++;
	}

	private static unsafe void InsertAsciiByte(ref ImGuiInputTextCallbackData d, int pos, byte c)
	{
		if (pos < 0 || pos > d.BufTextLen) return;
		if (d.BufTextLen + 1 >= d.BufSize) return;
		for (var i = d.BufTextLen; i > pos; i--) d.Buf[i] = d.Buf[i - 1];
		d.Buf[pos] = c;
		d.BufTextLen++;
		d.Buf[d.BufTextLen] = 0;
		d.BufDirty = 1;
	}
	private static unsafe int PrevUtf8Len(ref ImGuiInputTextCallbackData d, int pos)
	{
		var n = 1;
		while (pos - n > 0 && (d.Buf[pos - n] & 0xC0) == 0x80) n++;
		return n;
	}
	private static unsafe int NextUtf8Len(ref ImGuiInputTextCallbackData d, int pos)
	{
		var n = 1;
		while (pos + n < d.BufTextLen && (d.Buf[pos + n] & 0xC0) == 0x80) n++;
		return n;
	}

	private static void DrawFieldInput()
	{
		if (!fieldActive) return;
		try
		{
			var io = ImGui.GetIO();
			LoadFieldAlign();
			Vector4 drawBg, drawBorder, drawText, drawAccent, drawFocus;
			float drawRadius;
			var autoLayout = FieldLayoutModeForProfile(fieldDetectedProfile, fieldPageAccent);
			if (fieldLayoutMode == 0 && autoLayout > 0 &&
				TryGetFieldLayoutPreset(autoLayout, out drawBg, out drawBorder, out drawText,
					out drawAccent, out drawFocus, out drawRadius))
			{
				/* 自动识别到网页版本时，使用该版式预设，避免网页传来半更新颜色导致混色。 */
			}
			else if (fieldPageTheme)
			{
				drawBg = fieldPageBg;
				drawBorder = fieldPageBorder;
				drawText = fieldPageText;
				drawAccent = fieldPageAccent;
				drawFocus = fieldPageFocus;
				drawRadius = fieldPageRadius;
			}
			else
			{
				drawBg = fieldBg;
				drawBorder = fieldBorder;
				drawText = fieldText;
				drawAccent = fieldAccent;
				drawFocus = fieldFocus;
				drawRadius = fieldRadius;
			}
			var imGuiWantsText = io.WantTextInput;   // 记住 ImGui 自己的判断（下面会把它改成 true）
			var anchor = io.MousePos;
			var w = fieldW > 8f ? fieldW : 320f;
			var h = fieldH > 8f ? fieldH : 26f;
			bool rectOk = fieldRl > -8000f && fieldRl < 20000f && fieldRt > -8000f && fieldRt < 20000f;
			if (fieldFrames == 0)
			{
				if (fieldHasClick)
				{
					/* 鼠标点开的框：此刻鼠标就在框里，视口原点 = 鼠标位置 - 点击点在页面里的坐标*缩放。量下来记住。 */
					fieldOriginX = anchor.X - fieldCx * fieldScale;
					fieldOriginY = anchor.Y - fieldCy * fieldScale;
					fieldOriginValid = true;
				}
				else if (!fieldOriginValid || !rectOk)
				{
					/* 还没量过视口原点（或页面没给字段坐标）：退回老办法——把鼠标当成框中心 */
					var rxf = (fieldRx > 0f && fieldRx < w) ? fieldRx : w * 0.5f;
					var ryf = (fieldRy > 0f && fieldRy < h) ? fieldRy : h * 0.5f;
					fieldOriginX = anchor.X - rxf * fieldScale - (rectOk ? fieldRl * fieldScale : 0f);
					fieldOriginY = anchor.Y - ryf * fieldScale - (rectOk ? fieldRt * fieldScale : 0f);
				}
			}
			/* 页面点击和自动聚焦可能分成两条请求到达。位置每帧重算，
			   后到的 /click 一旦量出视口原点，下一帧就立即把覆盖框摆正，
			   不会把第一次用旧原点算出的位置一直冻结到下次聚焦。 */
			fieldScreenPos = new Vector2(
				fieldOriginX + (rectOk ? fieldRl : 0f) * fieldScale + fieldDx,
				fieldOriginY + (rectOk ? fieldRt : 0f) * fieldScale + fieldDy + fieldRequestDy * fieldScale);
			fieldSize = new Vector2(Math.Clamp(w * fieldScale, 60f, 2000f), Math.Clamp(h * fieldScale, 14f, 300f));
			/* 窗口比字段四周各大 2px：ImGui 按窗口矩形裁剪绘制，紧贴窗口底边的那一行会被裁掉，
			   原来 1px 的边框和下面两个圆角就是这么消失的。留 2px 余量后，再用同样的 WindowPadding
			   把内容原点推回字段左上角，盒子位置和以前一模一样（不用重新校准）。 */
			/* 位置和尺寸都取整：ImGui 的字是用线性采样画的，落在半像素上就会发虚
			   （校准里的 scale 常带小数，不取整的话整层字都是模糊的）。取整最多差 0.5px，肉眼无感。 */
			var winPos = new Vector2(MathF.Round(fieldScreenPos.X - 2f), MathF.Round(fieldScreenPos.Y - 2f));
			var boxW = MathF.Round(fieldSize.X);
			var boxH = MathF.Round(fieldSize.Y);
			ImGui.SetNextWindowPos(winPos, ImGuiCond.Always);
			ImGui.SetNextWindowSize(new Vector2(boxW + 4f, boxH + 4f), ImGuiCond.Always);
			/* 让覆盖层成为 ImGui 的"焦点窗口"（只需要在打开时做一次；每帧都强制焦点是没必要的折腾）。
			   注意：加过这一句之后 ImGui 才会把 io.WantTextInput 报成 true，Dalamud 也才会把按键转发进来。 */
			if (fieldFrames == 0) ImGui.SetNextWindowFocus();
			var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
				| ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse
				| ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground
				| ImGuiWindowFlags.NoDocking;
			/* 和页面的输入框严格对齐：尺寸与位置不随配色变化，颜色由页面按当前版本
			   发来的 token 决定；只有边框、左侧信号导轨和右上角标记承担状态提示。 */
			/* 页面里的字号是 14px、内边距 8/10px，但页面在游戏里是按 fieldScale 倍渲染的（校准里的 scale），
			   所以覆盖层的字号和内边距都跟着乘。字号是"按尺寸重新加载一份字体"，不是把 14px 的字形拉大
			   （拉大会又糊又胖）；只有改了校准里的缩放才会重建。 */
			var scale = Math.Clamp(fieldScale, 0.5f, 3f);
			var fnt = EnsureFieldFont(14f * scale * fieldFontScale);
			var fh = fnt != null ? fieldFontPx : ImGui.GetFontSize();   // 实际用的字号
			var padX = MathF.Round(10f * scale);                        // 页面 input/textarea 的左右内边距（取整防糊）
			var padY = fieldMulti ? MathF.Round(8f * scale)             // 多行：贴着顶（同页面 textarea）
				: MathF.Round(Math.Max(1f, (boxH - fh) / 2f));          // 单行：垂直居中
			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2f, 2f));
			ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(padX, padY));
			ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, drawRadius);
			ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
			/* 自带底板/边框交给下面的手绘（ImGui 自己的 FrameBg 会在窗口底边被裁，四角不齐）；
			   这里把它的各种状态色全设成透明，免得聚焦时又冒出一层别的颜色。 */
			ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0f, 0f, 0f, 0f));
			ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0f, 0f, 0f, 0f));
			ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0f, 0f, 0f, 0f));
			ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0f, 0f, 0f, 0f));
			ImGui.PushStyleColor(ImGuiCol.BorderShadow, new Vector4(0f, 0f, 0f, 0f));
			ImGui.PushStyleColor(ImGuiCol.Text, drawText);
			ImGui.Begin("##inpaFieldOverlay", flags);
			/* 这里原本每帧强行把 io.WantCaptureKeyboard / io.WantTextInput 写成 true。
			   实测没必要：输入框被"鼠标方式"激活后（见下面那次补点击），ImGui 自己就会把这两个标志报成 true，
			   而每帧强改反倒可能干扰输入法模块（它也在读这两个标志）。所以去掉了，保持和普通插件的输入框一致。 */
			/* 打开覆盖层的那次点击发生在覆盖层出现之前，ImGui 根本收不到 —— 于是：
			   ① 输入框只被"导航"激活，ImGui 不报 WantTextInput=true，Dalamud 就不转发按键
			      （字母、方向键、Ctrl+C/V 都要"再点一下"才行）；
			   ② 拖动选择没有"按下"这个锚点，只能从激活时的光标位置（内容末尾）开始选。
			   这里按当前鼠标按键状态补一次点击，把这两件事一起解决：
			     · 你还按着左键（点开就想拖动）→ 补"抬起+按下"，以"按下"收尾，不打断你的拖动；
			     · 左键已松开 → 补一整套"按下+抬起"，等价于在输入框上点了一下。 */
			if (fieldFrames == 1 && !calOpen && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
			{
				var mouseHeld = (GetAsyncKeyState(0x01) & 0x8000) != 0;
				io.AddMouseButtonEvent(0, false);
				io.AddMouseButtonEvent(0, true);
				if (!mouseHeld) io.AddMouseButtonEvent(0, false);
			}
			/* ImGui 绘制字时会乘一次全局缩放（你这里是 150% → 1.5）；位图是按目标像素光栅化的，
			   所以这里把多出来的那份除掉，保证"位图尺寸 = 绘制尺寸"，字不会被打虚。 */
			var gsNow = Math.Max(0.1f, Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
			/* 只在打开时设一次：窗口的 FontWindowScale 会一直保留，每帧重设没必要。 */
			if (fieldFrames == 0 && Math.Abs(gsNow - 1f) > 0.001f) ImGui.SetWindowFontScale(1f / gsNow);
			/* 只要这个框还"在用"就把它钉在键盘焦点上；鼠标点在字段外面才算结束。
			   不能用 IsWindowHovered()：编辑中它恒为 false，会把输入法层误关掉。 */
			bool needFocus = !calOpen && (fieldFrames < 3 || !fieldHadFocus);
			if (needFocus) ImGui.SetKeyboardFocusHere();
			ImGui.SetNextItemWidth(-1f);
			/* 自己把输入框的底板、边框、左侧信号导轨和右上角标画出来。
			   使用页面传来的背景 / 边框 / 文字 / 强调色，三版共用同一几何结构。
			   注意要画在 InputText 之前，字才压在底板上。 */
			var boxMin = ImGui.GetCursorScreenPos();
			var boxMax = new Vector2(boxMin.X + boxW, boxMin.Y + boxH);
			var fieldDl = ImGui.GetWindowDrawList();
			fieldDl.AddRectFilled(boxMin, boxMax, ImGui.GetColorU32(drawBg), drawRadius, ImDrawFlags.RoundCornersAll);
			var accentW = Math.Clamp(MathF.Round(4f * scale), 3f, 8f);
			fieldDl.AddRectFilled(
				boxMin,
				new Vector2(boxMin.X + accentW, boxMax.Y),
				ImGui.GetColorU32(drawAccent),
				drawRadius,
				ImDrawFlags.RoundCornersLeft);
			var borderThick = Math.Max(1f, MathF.Round(scale));
			fieldDl.AddRect(
				new Vector2(boxMin.X + 0.5f, boxMin.Y + 0.5f),
				new Vector2(boxMax.X - 0.5f, boxMax.Y - 0.5f),
				ImGui.GetColorU32(drawFocus),
				Math.Max(0f, drawRadius - 0.5f),
				ImDrawFlags.RoundCornersAll,
				borderThick);
			var mark = Math.Clamp(MathF.Round(6f * scale), 4f, 12f);
			var markInset = Math.Clamp(MathF.Round(7f * scale), 5f, 14f);
			var markX = boxMax.X - markInset;
			var markY = boxMin.Y + markInset;
			fieldDl.AddLine(new Vector2(markX - mark, markY), new Vector2(markX, markY), ImGui.GetColorU32(drawAccent), 1f);
			fieldDl.AddLine(new Vector2(markX, markY), new Vector2(markX, markY + mark), ImGui.GetColorU32(drawAccent), 1f);
			bool enter;
			var bufBefore = fieldBuf;
			/* 只有真的攒了"待补/待删"动作时才挂回调 —— 正常打字这条路径就跟普通插件的
			   ImGui.InputText 一模一样，不引入任何额外变量（避免干扰输入法的组词）。 */
			var needCallback = fieldPendingText.Length > 0 || fieldPendingBack > 0 || fieldPendingDel > 0;
			var extraFlags = needCallback ? ImGuiInputTextFlags.CallbackAlways : ImGuiInputTextFlags.None;
			ImGui.ImGuiInputTextCallbackDelegate? cb =
				needCallback ? OnFieldTextCallback : (ImGui.ImGuiInputTextCallbackDelegate?)null;
			if (fieldMulti)
			{
				/* 多行框（页面里的 textarea，比如「宏内容」）：框高跟页面那个框一致，
				   回车就是换行，Ctrl+回车 / Shift+回车 / Esc 才结束编辑。 */
				var msz = new Vector2(-1f, Math.Max(20f, boxH - 2f));
				if (fnt != null)
				{
					using (fnt.Push())
					{
						ImGui.InputTextMultiline("##inpaFieldText", ref fieldBuf, 8192, msz, extraFlags, cb);
					}
				}
				else
				{
					ImGui.InputTextMultiline("##inpaFieldText", ref fieldBuf, 8192, msz, extraFlags, cb);
				}
				enter = (ImGui.IsKeyPressed(ImGuiKey.Enter) && (io.KeyCtrl || io.KeyShift)) || ImGui.IsKeyPressed(ImGuiKey.Escape);
			}
			else if (fnt != null)
			{
				using (fnt.Push())
				{
					enter = ImGui.InputTextWithHint("##inpaFieldText", fieldPh, ref fieldBuf, 2048,
						ImGuiInputTextFlags.EnterReturnsTrue | extraFlags, cb);
				}
			}
			else
			{
				enter = ImGui.InputTextWithHint("##inpaFieldText", fieldPh, ref fieldBuf, 2048,
					ImGuiInputTextFlags.EnterReturnsTrue | extraFlags, cb);
			}
			var hadFocusNow = ImGui.IsItemActive() || ImGui.IsItemFocused();
			/* 文本最近有没有被"外部"改过？输入法选词/上屏就发生在按下的那一瞬间，
			   等我们几帧后再看 composition 已经结束，只能靠这个判据识别。 */
			if (fieldBuf != fieldBufPrev)
			{
				if (fieldSelfEdited) fieldSelfEdited = false;      // 我们自己补的，不算外部改动
				else fieldBufChangedFrame = fieldFrames;
				fieldBufPrev = fieldBuf;
			}
			var imeTouchedRecently = (fieldFrames - fieldBufChangedFrame) <= 6;   // 约 100ms 内文本被外部改过
			/* 物理按键兜底（防重复版）：
			   物理轮询（GetAsyncKeyState）比 Dalamud 处理按键消息早一帧左右，直接补会和 ImGui 自己的插入重复
			   （出现过按一下出 3~4 个数字）。所以按下后先"挂起 3 帧"：
			     · 这 3 帧内 ImGui 收到了这个键 → 撤销兜底，交给 ImGui；
			     · 3 帧都没收到 → 说明 Dalamud 把它拦下没转发，这时才自己补。 */
			if (fieldActive && fieldHadFocus)
			{
				var composing = IsImeComposing();
				fieldImeComposing = composing;
				if (composing)
				{
					/* 组词中：清掉所有"待判断/待执行"的按键状态，全部交给输入法 */
					fieldPendingText = ""; fieldPendingBack = 0; fieldPendingDel = 0;
					for (int d0 = 0; d0 <= 9; d0++) fieldDigitWaitFrame[d0] = -1;
					fieldBackWaitFrame = -1; fieldDelWaitFrame = -1; fieldEnterWaitFrame = -1; fieldEscWaitFrame = -1;
				}
				else
				{
				for (int d = 0; d <= 9; d++)
				{
					var imGuiHas = ImGui.IsKeyDown(ImGuiKey.Key0 + d) || ImGui.IsKeyDown(ImGuiKey.Keypad0 + d);
					var vkTop = 0x30 + d;    // 顶排 0-9
					var vkNumpad = 0x60 + d; // 小键盘 0-9
					var physDown = ((GetAsyncKeyState(vkTop) & 0x8000) != 0) || ((GetAsyncKeyState(vkNumpad) & 0x8000) != 0);
					var justDown = physDown && !fieldRawDigitDown[d];
					fieldRawDigitDown[d] = physDown;
					if (justDown)
					{
						fieldDigitWaitFrame[d] = fieldFrames;
						fieldDigitSawImGui[d] = imGuiHas;
						fieldDigitBuf0[d] = fieldBuf.Length;
						/* 按下瞬间的修饰键状态：Shift 决定补 @ 还是 2；Ctrl/Alt 按住则是快捷键，别插字 */
						var sh = (GetAsyncKeyState(0x10) & 0x8000) != 0;
						var ct = (GetAsyncKeyState(0x11) & 0x8000) != 0;
						var al = (GetAsyncKeyState(0x12) & 0x8000) != 0;
						fieldDigitShifted[d] = sh;
						fieldDigitSkip[d] = (ct && !al) || (al && !ct);
						continue;
					}
					if (fieldDigitWaitFrame[d] < 0) continue;
					if (imGuiHas) fieldDigitSawImGui[d] = true;
					if (fieldFrames - fieldDigitWaitFrame[d] < 3) continue;   // 再等两帧，看 ImGui 会不会收到
					if (!fieldDigitSawImGui[d])
					{
						if (fieldBuf.Length != fieldDigitBuf0[d])
						{
							/* 文本已经变了：这个键被输入法（或别的路径）处理过了，比如中文输入法打出的全角「（」 */
							Audit("输入框：数字键 " + d + " 已由输入法处理，跳过物理兜底");
						}
						else if (imeTouchedRecently)
						{
							/* 刚才文本被改过（多半是输入法用这个数字选了候选词）：别再补一个数字 */
							Audit("输入框：数字键 " + d + " 期间输入法刚上屏，跳过物理兜底");
					}
					else if (!fieldDigitSkip[d])
					{
						var ch = fieldDigitShifted[d] ? ")!@#$%^&*("[d] : (char)('0' + d);
						fieldPendingText += ch;
						if (fieldFrames - fieldLastFallbackLog > 1500)   // 每 25 秒最多记一条，避免刷屏
						{
							fieldLastFallbackLog = fieldFrames;
							Audit("输入框：物理兜底补入字符 " + ch + "（等 3 帧 ImGui 仍未收到，Shift=" + fieldDigitShifted[d] + "）");
						}
					}
					}
					fieldDigitWaitFrame[d] = -1;
				}
				/* Backspace / Delete 同理 */
				var backImGui = ImGui.IsKeyDown(ImGuiKey.Backspace);
				var backPhys = (GetAsyncKeyState(0x08) & 0x8000) != 0;
				var backJust = backPhys && !fieldRawBackDown;
				fieldRawBackDown = backPhys;
				if (backJust) { fieldBackWaitFrame = fieldFrames; fieldBackSawImGui = backImGui; fieldBackBuf0 = fieldBuf.Length; }
				else if (fieldBackWaitFrame >= 0)
				{
					if (backImGui) fieldBackSawImGui = true;
					if (fieldFrames - fieldBackWaitFrame >= 3)
					{
						if (!fieldBackSawImGui && !imeTouchedRecently && fieldBuf.Length == fieldBackBuf0 && fieldBuf.Length > 0)
						{
							fieldPendingBack++;
							if (fieldFrames - fieldLastFallbackLog > 1500)
							{
								fieldLastFallbackLog = fieldFrames;
								Audit("输入框：物理兜底退格（等 3 帧 ImGui 仍未收到）");
							}
						}
						fieldBackWaitFrame = -1;
					}
				}
				var delImGui = ImGui.IsKeyDown(ImGuiKey.Delete);
				var delPhys = (GetAsyncKeyState(0x2E) & 0x8000) != 0;
				var delJust = delPhys && !fieldRawDelDown;
				fieldRawDelDown = delPhys;
				if (delJust) { fieldDelWaitFrame = fieldFrames; fieldDelSawImGui = delImGui; fieldDelBuf0 = fieldBuf.Length; }
				else if (fieldDelWaitFrame >= 0)
				{
					if (delImGui) fieldDelSawImGui = true;
					if (fieldFrames - fieldDelWaitFrame >= 3)
					{
						if (!fieldDelSawImGui && !imeTouchedRecently && fieldBuf.Length == fieldDelBuf0 && fieldBuf.Length > 0)
						{
							fieldPendingDel++;
							if (fieldFrames - fieldLastFallbackLog > 1500)
							{
								fieldLastFallbackLog = fieldFrames;
								Audit("输入框：物理兜底 Delete（等 3 帧 ImGui 仍未收到）");
							}
						}
						fieldDelWaitFrame = -1;
					}
				}
				/* 回车 / Esc：单击状态下 ImGui 同样收不到，这里也做"等 3 帧"的兜底 */
				var enterImGui = ImGui.IsKeyDown(ImGuiKey.Enter) || ImGui.IsKeyDown(ImGuiKey.KeypadEnter);
				var enterPhys = (GetAsyncKeyState(0x0D) & 0x8000) != 0;
				var enterJust = enterPhys && !fieldRawEnterDown;
				fieldRawEnterDown = enterPhys;
				if (enterJust) { fieldEnterWaitFrame = fieldFrames; fieldEnterSawImGui = enterImGui; fieldEnterBuf0 = fieldBuf.Length; }
				else if (fieldEnterWaitFrame >= 0)
				{
					if (enterImGui) fieldEnterSawImGui = true;
					if (fieldFrames - fieldEnterWaitFrame >= 3)
					{
						/* 回车：如果刚刚有"输入法上屏"（多半这次回车就是用来提交拼音/英文的）→ 只上屏、不关闭输入框，
						   这样中英混输不用重新点一次；没有组词时回车照常结束输入。 */
						if (!fieldEnterSawImGui && !imeTouchedRecently)
						{
							if (fieldMulti)
							{
								fieldPendingText += "\n";   // 多行框：回车是换行
								Audit("输入框：物理兜底换行（Enter）");
							}
							else if (fieldKeepOnEnter)
							{
								fieldEnterSeq++;            // 聊天框：回车=发送，框不关
								fieldBuf = string.Empty;
								fieldBufPrev = fieldBuf;
								fieldBufChangedFrame = -9999;
								fieldSelfEdited = true;
								Audit("输入框：回车发送（聊天框，物理兜底）");
							}
							else
							{
								Audit("输入框：物理兜底结束输入（Enter）");
								fieldActive = false;
							}
						}
						fieldEnterWaitFrame = -1;
					}
				}
				var escImGui = ImGui.IsKeyDown(ImGuiKey.Escape);
				var escPhys = (GetAsyncKeyState(0x1B) & 0x8000) != 0;
				var escJust = escPhys && !fieldRawEscDown;
				fieldRawEscDown = escPhys;
				if (escJust) { fieldEscWaitFrame = fieldFrames; fieldEscSawImGui = escImGui; }
				else if (fieldEscWaitFrame >= 0)
				{
					if (escImGui) fieldEscSawImGui = true;
					if (fieldFrames - fieldEscWaitFrame >= 3)
					{
						if (!fieldEscSawImGui && !imeTouchedRecently)
						{
							Audit("输入框：物理兜底结束输入（Esc）");
							fieldEscSeq++;
							fieldActive = false;
						}
						fieldEscWaitFrame = -1;
					}
				}
				}   // end: 非组词状态下的物理兜底
			}
			else if (fieldFrames == 5 && (io.KeyCtrl || io.KeyAlt || io.KeySuper))
			{
				/* 开始编辑时修饰键就是按下状态：典型的"Ctrl 被游戏卡住"（会导致数字/字母都打不进去） */
				Audit("输入框：开始编辑时修饰键处于按下状态 Ctrl=" + io.KeyCtrl + " Alt=" + io.KeyAlt + " Shift=" + io.KeyShift + " Super=" + io.KeySuper);
			}
			/* 覆盖层还开着、输入框却丢了键盘焦点：这就是"按键失灵"的时刻，记一笔（下一帧会自动抢回） */
			if (fieldFrames > 3 && !hadFocusNow && !fieldFocusLostLogged)
			{
				Audit("输入框：丢了键盘焦点（第 " + fieldFrames + " 帧），下一帧抢回");
				fieldFocusLostLogged = true;
			}
			else if (hadFocusNow)
			{
				fieldFocusLostLogged = false;
			}
			fieldHadFocus = hadFocusNow;
			fieldFrames++;
			var p0 = new Vector2(fieldScreenPos.X - 8f, fieldScreenPos.Y - 8f);
			var p1 = new Vector2(fieldScreenPos.X + fieldSize.X + 8f, fieldScreenPos.Y + fieldSize.Y + 8f);
			bool overField = ImGui.IsMouseHoveringRect(p0, p1);
			/* 结束输入：回车 / Esc / 点到框外。只在"刚开就关"这种异常情况才记日志，正常关闭不吵人 */
			var shortSession = fieldFrames < 12;
			if (enter || ImGui.IsKeyPressed(ImGuiKey.Escape))
			{
				if (enter && fieldKeepOnEnter)
				{
					/* 聊天框：回车 = 发送。这里只把"我收到回车了"记一笔、清空内容，
					   输入框不关（下一帧会自动抢回焦点），可以接着打下一条。 */
					fieldEnterSeq++;
					fieldBuf = string.Empty;
					fieldBufPrev = fieldBuf;
					fieldBufChangedFrame = -9999;
					fieldSelfEdited = true;
					Audit("输入框：回车发送（聊天框，第 " + fieldFrames + " 帧）");
				}
				else
				{
					if (shortSession) Audit("输入框：刚开就结束（" + (enter ? "回车/快捷键" : "Esc") + "，第 " + fieldFrames + " 帧）");
					if (!enter) fieldEscSeq++;
					fieldActive = false;
				}
			}
			else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !overField)
			{
				if (shortSession) Audit("输入框：刚开就结束（鼠标点到框外，第 " + fieldFrames + " 帧）");
				fieldActive = false;
			}
			ImGui.End();
			ImGui.PopStyleColor(6);
			ImGui.PopStyleVar(4);
		}
		catch (Exception e)
		{
			fieldActive = false;
			Log.Debug(e, "INPA: 页面输入框覆盖层失败");
		}
	}
	private static void OnInpaLockCommand(string command, string args) { BwLockToggle(); }
	private static void OnInpaReloadCommand(string command, string args) { BwOverlay("reload"); }
	private static void OnInpaCalCommand(string command, string args)
	{
		OpenConfigTab(1);
		ChatGui.Print("INPA：已打开「输入框设置」");
	}
		private static void OnInpaCommand(string command, string args)
	{
		var raw = (args ?? string.Empty).Trim();
		var sp = raw.IndexOf(char.Parse(" "));
		var head = (sp < 0 ? raw : raw.Substring(0, sp)).ToLowerInvariant();
		var rest = (sp < 0) ? "" : raw.Substring(sp + 1).Trim();
		switch (head)
		{
			case "cal":
				OpenConfigTab(1);
				ChatGui.Print("INPA：已打开「输入框设置」");
				break;
			case "show": BwOverlay("hidden on"); break;
			case "hide": BwOverlay("hidden off"); break;
			case "lock": BwLockToggle(); break;
			case "lockon": BwLock(true); break;
			case "lockoff": BwLock(false); break;
			case "unlock": BwLock(false); break;
case "reload": BwOverlay("reload"); break;
			case "toggle":
			case "": BwOverlay("hidden toggle"); break;
			default:
				Log.Information("INPA: 用法 /inpa [show|hide|lock|lockon|lockoff|reload]，只打 /inpa 是显示/隐藏");
				break;
		}
	}
	
	/// <summary>把设置转发给 Browsingway（它注册的就是 /bw 这个 Dalamud 命令）。</summary>
	/// <summary>锁 / 解锁切换：绑定成一个游戏内宏就能一键来回切。</summary>
	private static void BwLockToggle()
	{
		BwOverlay("locked toggle");
		PrintLockState();
	}
	
	private static void BwLock(bool on)
	{
		BwOverlay(on ? "locked on" : "locked off");
		PrintLockState();
	}
	
	/// <summary>发完命令回读一次 Browsingway 配置，在聊天里说清现在是锁着还是没锁。</summary>
	private static void PrintLockState()
	{
		try
		{
			foreach (var cfg in BrowsingwayConfigs())
			{
				if (cfg == null || !File.Exists(cfg)) continue;
				using var doc = JsonDocument.Parse(File.ReadAllText(cfg, Encoding.UTF8));
				if (!doc.RootElement.TryGetProperty("Inlays", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
				foreach (var inlay in arr.EnumerateArray())
				{
					if (inlay.ValueKind != JsonValueKind.Object) continue;
					if (!inlay.TryGetProperty("Url", out var uEl) || !(uEl.GetString() ?? "").Contains("18500")) continue;
					bool locked = inlay.TryGetProperty("Locked", out var lEl) && lEl.ValueKind == JsonValueKind.True;
					ChatGui.Print("INPA：游戏内窗口位置 = " + (locked ? "已锁定（页面里可以正常拖动/滚动）" : "未锁定（在页面上拖动会移动窗口）"));
					return;
				}
			}
		}
		catch (Exception e)
		{
			Log.Debug(e, "INPA: 回读 Browsingway 锁定状态失败");
		}
	}
		private static void BwOverlay(string arg)
	{
		try
		{
			var name = BwOverlayName();
			var ok = CommandManager.ProcessCommand("/bw overlay " + name + " " + arg);
			Log.Information("INPA: /bw overlay {0} {1} -> {2}", name, arg, ok);
		}
		catch (Exception e)
		{
			Log.Warning(e, "INPA: 调用 /bw 失败（没装 Browsingway？）");
		}
	}
	
	// ---------------------------------------------------------------- 包间提醒
	// 以前这个"脑子"在网页里：窗口一关就不提醒了。现在挪到插件（游戏开着就一直在），
	// 数据从共用文件里读（roomLog + roomAlert），触发过的阶段记在内存里，不重复刷屏。

	private static readonly HashSet<string> roomFired = new HashSet<string>();

	/// <summary>当前这个页面属于哪个店（从 app.html 里的 shop: "..." 读）。用于提醒文案。</summary>
	private static string shopLabelCache = "INPA";
	private static string? shopLabelFor;

	private static string ShopLabel()
	{
		try
		{
			var p = ResolvePagePath();
			if (p == null) return shopLabelCache;
			if (shopLabelFor == p) return shopLabelCache;
			var text = File.ReadAllText(p, Encoding.UTF8);
			var m = Regex.Match(text, "shop\\s*:\\s*\"([^\"]{1,40})\"");
			shopLabelCache = m.Success ? m.Groups[1].Value : "INPA";
			shopLabelFor = p;
			return shopLabelCache;
		}
		catch { return shopLabelCache; }
	}

	private async Task RoomAlertLoopAsync(CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			try { CheckRoomsOnce(); }
			catch (Exception e) { Log.Debug(e, "INPA Bridge: 包间提醒检查失败"); }
			try { await Task.Delay(10000, token).ConfigureAwait(false); }
			catch { break; }
		}
	}

	private void CheckRoomsOnce()
	{
		string? pageHtml = null;
		try
		{
			var page = ResolvePagePath();
			if (page != null) pageHtml = File.ReadAllText(page, Encoding.UTF8);
		}
		catch { }

		var source = RoomAlertPolicy.ResolveSource(
			pageHtml,
			profile => StateFile(profile),
			StateFile(),
			ShopLabel());
		if (source == null) return;

		var notices = RoomAlertPolicy.Scan(
			source.DataPath,
			source.ShopLabel,
			DateTimeOffset.Now.ToUnixTimeMilliseconds(),
			roomFired);
		foreach (var notice in notices)
		{
			NotifyGame(notice.Text);
			Audit(notice.AuditText);
		}
	}

	/// <summary>往游戏聊天里发一条只有自己看得见的默语（/echo）。</summary>
	private static void NotifyGame(string text)
	{
		try
		{
			_ = RunOnFramework(() =>
			{
				try { SendToChatBox("/echo " + text); }
				catch (Exception e) { Log.Warning(e, "INPA Bridge: 发包间提醒失败"); }
				return 0;
			});
		}
		catch (Exception e) { Log.Warning(e, "INPA Bridge: 包间提醒调度失败"); }
	}
	// ------------------------------------------------------------------ /state
	// 网页的业务数据（流水 / 店员 / 包间 / 宏 / 设置）统一存成这一个 json 文件，
	// 「浏览器版」和「游戏内窗口」于是读写同一份，不再各存各的。
	// 文件格式：{"rev":N,"updatedAt":"...","data":{...}}；rev 用来挡住并发覆盖。

	private static readonly object StateLock = new object();
	private static string? statePathCache;
	private static string? statePathFor;     // 上面这个路径是按哪个页面文件算出来的

	/* ---------------------------------------------------------------- 访问口令 + 来源白名单
	   插件监听在本机（127.0.0.1），但浏览器里任何网页都能跨域调它；以前 CORS 是 * 且没有任何口令，
	   等于你随手打开的网页都能发起交易 / 代你发聊天。现在收紧：
	     · 口令：数据目录下 inpa-token.txt（首次自动生成）。请求带 ?t=口令 或 X-InPA-Token 头。
	     · 来源：本机页面（127.0.0.1 / localhost / [::1]，任意端口）自动放行；
	             托管网页必须带通用档案口令，不依赖浏览器来源。
	     · /ping 和插件自己发的页面不校验口令（页面靠 /ping 探测连接；它自己发的页面里带了口令）。
	     · 插件发页面时会把这个口令注入进去（window.__INPA_TOKEN），本机模式用起来无感。 */
	private static string? tokenCache;
	private static string? tokenCacheDir;   // 这份口令是哪个店的——两个店各有各的 inpa-token.txt，切店后必须重读

	private static string? DataDir(string webProfile = "")
	{
		try
		{
			if (webProfile.Length > 0) return Path.Combine(WebDataRoot(webProfile), "data");
			var f = StateFile();
			return string.IsNullOrEmpty(f) ? null : Path.GetDirectoryName(f);
		}
		catch { return null; }
	}

	/// <summary>本机访问口令：数据目录里的 inpa-token.txt，没有就生成一个。</summary>
	internal static string AccessToken(string webProfile = "")
	{
		try
		{
			var dir = DataDir(webProfile);
			if (string.IsNullOrEmpty(dir)) return "";
			/* 缓存要跟着"是哪个店"走：以前只判断 tokenCache 非空，切店（page.txt 指向另一个 app.html）
			   之后还沿用上一个店的口令 —— 于是两个店显示/校验的都是同一个口令。 */
			if (tokenCache != null && tokenCacheDir == dir) return tokenCache;
			Directory.CreateDirectory(dir!);
			var file = Path.Combine(dir!, "inpa-token.txt");
			if (File.Exists(file))
			{
				var t = File.ReadAllText(file, Encoding.UTF8).Trim();
				if (t.Length > 0) { tokenCacheDir = dir; return tokenCache = t; }
			}
			var bytes = RandomNumberGenerator.GetBytes(24);
			var made = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
			File.WriteAllText(file, made, new UTF8Encoding(false));
			try { Log.Information("INPA Bridge: 已生成访问口令（网页连接时要填）：{0}", new object[1] { made }); } catch { }
			tokenCacheDir = dir;
			return tokenCache = made;
		}
		catch (Exception e)
		{
			Log.Warning(e, "INPA Bridge: 读写访问口令失败");
			return "";
		}
	}

	private static bool OriginAllowed(string origin)
	{
		if (string.IsNullOrEmpty(origin)) return true;   // 同源 GET / 本机程序 / curl：没有 Origin
		/* file:// 页面（Origin: null）必须通过口令，不能仅凭来源放行。 */
		if (origin.Trim().Equals("null", StringComparison.OrdinalIgnoreCase)) return false;
		var o = origin.TrimEnd('/').ToLowerInvariant();
		try
		{
			var host = new Uri(o).Host.ToLowerInvariant();
			if (host == "127.0.0.1" || host == "localhost" || host == "::1" || host == "[::1]") return true;   // 本机页面
		}
		catch { }
		return false;
	}

	private static bool TokenOk(HttpListenerRequest req, string webProfile)
	{
		if (string.IsNullOrEmpty(webProfile)) return false;
		var tok = AccessToken(webProfile);
		if (tok.Length == 0) return false;
		var got = req.QueryString["t"];
		if (string.IsNullOrEmpty(got)) got = req.Headers["X-InPA-Token"];
		if (string.IsNullOrEmpty(got)) return false;
		var expected = Encoding.UTF8.GetBytes(tok);
		var actual = Encoding.UTF8.GetBytes(got);
		return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
	}

	/* ---------------------------------------------------------------- WebSocket 实时推送（/ws）
	   页面连上来之后，插件把「收到密聊 / 交易事件 / 数据保存」直接推过去，
	   页面就不用每 1.5 秒轮询（轮询保留做兜底）。用的是 HttpListener 自带的 WebSocket 支持。
	   发送走一个后台队列：同一时刻只有一个发送者，绝不阻塞游戏主线程。 */
	private static readonly List<WebSocket> WsClients = new List<WebSocket>();
	private static readonly object WsLock = new object();
	private static readonly ConcurrentQueue<string> WsQueue = new ConcurrentQueue<string>();
	private static int wsSenderRunning;
	private static int wsConnectedCount;

	private static string JsonStr(string? s)
	{
		try { return JsonSerializer.Serialize(s ?? string.Empty); } catch { return "\"\""; }
	}

	/// <summary>把一条 JSON 推给所有连着的页面（不阻塞调用方）。</summary>
	private static void WsBroadcast(string json)
	{
		if (string.IsNullOrEmpty(json)) return;
		WsQueue.Enqueue(json);
		if (Interlocked.CompareExchange(ref wsSenderRunning, 1, 0) != 0) return;
		_ = Task.Run(async delegate
		{
			try
			{
				while (true)
				{
					string msg;
					if (!WsQueue.TryDequeue(out msg))
					{
						await Task.Delay(30).ConfigureAwait(continueOnCapturedContext: false);
						lock (WsLock) { if (WsQueue.IsEmpty) { wsSenderRunning = 0; return; } }
						continue;
					}
					byte[] bytes = Encoding.UTF8.GetBytes(msg);
					WebSocket[] targets;
					lock (WsLock) targets = WsClients.ToArray();
					List<WebSocket>? dead = null;
					foreach (WebSocket s in targets)
					{
						try
						{
							if (s.State != WebSocketState.Open) { (dead ?? (dead = new List<WebSocket>())).Add(s); continue; }
							await s.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
						}
						catch { (dead ?? (dead = new List<WebSocket>())).Add(s); }
					}
					if (dead != null)
					{
						lock (WsLock) { foreach (WebSocket d in dead) { WsClients.Remove(d); try { d.Dispose(); } catch { } } wsConnectedCount = WsClients.Count; }
					}
				}
			}
			catch (Exception e) { Log.Debug(e, "INPA Bridge: WebSocket 推送失败"); }
			finally { Interlocked.Exchange(ref wsSenderRunning, 0); }
		});
	}

	private async Task HandleWsAsync(HttpListenerContext ctx)
	{
		WebSocket? socket = null;
		try
		{
			var wsCtx = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(continueOnCapturedContext: false);
			socket = wsCtx.WebSocket;
			lock (WsLock) { WsClients.Add(socket); wsConnectedCount = WsClients.Count; }
			Log.Information("INPA Bridge: 页面已连上实时推送（WebSocket）");
			var buf = new byte[2048];
			while (socket.State == WebSocketState.Open)
			{
				var r = await socket.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
				if (r.MessageType == WebSocketMessageType.Close) break;
			}
		}
		catch (Exception e) { Log.Debug(e, "INPA Bridge: WebSocket 断开"); }
		finally
		{
			if (socket != null)
			{
				lock (WsLock) { WsClients.Remove(socket); wsConnectedCount = WsClients.Count; }
				try { socket.Dispose(); } catch { }
			}
		}
	}
	private static long lastBackupTicks;
	private static long stateGetCount;
	private static long statePostCount;
	private static string lastStateGetAt = "";
	private static string lastStatePostAt = "";

	/// <summary>数据文件位置：app.html 同级的 data\inpa-data.json（和启动器共用）。</summary>
	private static string? StateFile(string webProfile = "")
	{
		if (webProfile.Length > 0) return Path.Combine(WebDataRoot(webProfile), "data", "inpa-data.json");
		lock (StateLock)
		{
			string? page = null;
			try
			{
				page = ResolvePagePath();
			}
			catch { }
			/* 页面文件暂时找不到（换包 / 重新解压 / page.txt 指向了不存在的文件）→ 兜底用插件目录，
			   但**不缓存**这个兜底结果：否则等页面文件回来了（路径还是原来那个），数据还会一直写在
			   插件目录里，直到用户手动切一次页面 —— 对方就会看到"页面旁边没有 data 文件夹"。 */
			if (string.IsNullOrEmpty(page))
			{
				var fallback = PluginDirectory();
				return string.IsNullOrEmpty(fallback) ? null : Path.Combine(fallback!, "data", "inpa-data.json");
			}
			/* 缓存跟着"是哪个页面"走：页面换了 / 页面路径变了就重算 */
			if (statePathCache != null && statePathFor == page) return statePathCache;
			var dir = Path.GetDirectoryName(page);
			if (string.IsNullOrEmpty(dir)) return null;
			statePathCache = Path.Combine(dir!, "data", "inpa-data.json");
			statePathFor = page;
			return statePathCache;
		}
	}

	private static object HandleStateGet(string webProfile)
	{
		lock (StateLock)
		{
			try
			{
				var path = StateFile(webProfile);
				if (path == null) return new { ok = false, error = "找不到数据文件位置" };
				if (!File.Exists(path)) return new { ok = true, rev = 0L, updatedAt = "", data = (JsonElement?)null, path };
				using var doc = JsonDocument.Parse(ReadStateText(path));
				var root = doc.RootElement;
				long rev = (root.TryGetProperty("rev", out var r) && r.ValueKind == JsonValueKind.Number) ? r.GetInt64() : 0L;
				string updatedAt = root.TryGetProperty("updatedAt", out var u) ? (u.GetString() ?? "") : "";
				JsonElement? data = (root.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null) ? d.Clone() : (JsonElement?)null;
				return new { ok = true, rev, updatedAt, data, path };
			}
			catch (Exception e)
			{
				Log.Error(e, "INPA Bridge: 读取数据文件失败");
				return new { ok = false, error = e.Message };
			}
		}
	}

	private async Task<object> HandleStatePostAsync(HttpListenerRequest req, string webProfile)
	{
		string body;
		using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
			body = await reader.ReadToEndAsync().ConfigureAwait(false);

		long rev = 0L;
		bool force = false;
		string? dataJson = null;
		try
		{
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;
			if (root.TryGetProperty("rev", out var r) && r.ValueKind == JsonValueKind.Number) rev = r.GetInt64();
			if (root.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True) force = true;
			if (root.TryGetProperty("data", out var d)) dataJson = d.GetRawText();
		}
		catch (Exception e)
		{
			return new { ok = false, error = "bad json: " + e.Message };
		}

		if (dataJson == null) return new { ok = false, error = "data 为空" };

		lock (StateLock)
		{
			try
			{
				var path = StateFile(webProfile);
				if (path == null) return new { ok = false, error = "找不到数据文件位置" };
				/* 插件与 exe 是两个进程：进程内锁只能挡住各自线程，必须再用同一把跨进程文件锁包住整个读-改-写事务。 */
				using var stateFileLock = AcquireStateWriteLock(path);

				long cur = 0L;
				string curUpdated = "";
				JsonElement? curData = null;
				if (File.Exists(path))
				{
					using var doc = JsonDocument.Parse(ReadStateText(path));
					var root = doc.RootElement;
					cur = (root.TryGetProperty("rev", out var r2) && r2.ValueKind == JsonValueKind.Number) ? r2.GetInt64() : 0L;
					curUpdated = root.TryGetProperty("updatedAt", out var u2) ? (u2.GetString() ?? "") : "";
					if (root.TryGetProperty("data", out var d2) && d2.ValueKind != JsonValueKind.Null) curData = d2.Clone();
				}

				if (rev != cur && !force)
				{
					Audit("数据保存被拦下：窗口 rev=" + rev + "，文件 rev=" + cur);
					return new { ok = false, conflict = true, rev = cur, updatedAt = curUpdated, data = curData, path };
				}

				var now = DateTime.Now;
				long newRev = cur + 1;
				var stamp = now.ToString("yyyy-MM-dd HH:mm:ss");
				var json = "{\"rev\":" + newRev + ",\"updatedAt\":\"" + stamp + "\",\"data\":" + dataJson + "}";

				var dir = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
				if (File.Exists(path)) TryBackupState(path);
				ReplaceStateFile(path, json);

				/* 数据刚存过：告诉开着的页面"变新了"，它们可以立刻去同步（不用等 3 秒轮询） */
				WsBroadcast("{\"t\":\"state\",\"rev\":" + newRev + "}");
				return new { ok = true, rev = newRev, updatedAt = stamp, path };
			}
			catch (Exception e)
			{
				Log.Error(e, "INPA Bridge: 写数据文件失败");
				return new { ok = false, error = e.Message };
			}
		}
	}

	/// <summary>读数据文件：插件和 exe 两个进程可能同时读写同一个文件，
	/// 对方正好在替换文件时会短暂"被占用" → 退避重试几次，别当成错误。</summary>
	private static string ReadStateText(string path)
	{
		for (int i = 0; ; i++)
		{
			try { return File.ReadAllText(path, Encoding.UTF8); }
			catch (IOException) { if (i >= 40) throw; Thread.Sleep(25); }
			catch (UnauthorizedAccessException) { if (i >= 40) throw; Thread.Sleep(25); }
		}
	}

	/// <summary>原子地换掉数据文件。插件(exe)和启动器可能同时写同一个 inpa-data.json：
	/// 以前两边都用固定的 inpa-data.json.tmp —— 一个正在写、另一个也来写，就报
	/// "The process cannot access the file ... .tmp"。现在临时文件名带进程号+GUID，
	/// 最后一步 Move 遇到"被占用"就退避重试（最多约 1 秒）。</summary>
	private static void ReplaceStateFile(string path, string json)
	{
		WriteDurableFile(path, new UTF8Encoding(false).GetBytes(json));
	}

	/// <summary>跨进程事务锁：插件和启动器共用同一个 .lock 文件。</summary>
	private static FileStream AcquireStateWriteLock(string path)
	{
		var dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
		var lockPath = path + ".lock";
		for (int i = 0; ; i++)
		{
			try
			{
				return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			}
			catch (IOException)
			{
				if (i >= 200) throw;
				Thread.Sleep(20);
			}
			catch (UnauthorizedAccessException)
			{
				if (i >= 200) throw;
				Thread.Sleep(20);
			}
		}
	}

	/// <summary>先写到唯一临时文件并 Flush(true)，再原子替换；断电时不会留下半截目标文件。</summary>
	private static void WriteDurableFile(string path, byte[] bytes)
	{
		var dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
		var tmp = path + "." + System.Diagnostics.Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
		using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
		{
			fs.Write(bytes, 0, bytes.Length);
			fs.Flush(true);
		}
		try
		{
			for (int i = 0; ; i++)
			{
				try
				{
					File.Move(tmp, path, true);
					return;
				}
				catch (IOException)
				{
					if (i >= 40) throw;
					Thread.Sleep(25);
				}
				catch (UnauthorizedAccessException)
				{
					if (i >= 40) throw;
					Thread.Sleep(25);
				}
			}
		}
		finally
		{
			try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
		}
	}

	/// <summary>写之前顺手留一份备份（最多 10 分钟一次，最多留 30 份）。</summary>
	private static void TryBackupState(string path)
	{
		try
		{
			var now = DateTime.Now;
			if (now.Ticks - lastBackupTicks < TimeSpan.FromMinutes(10).Ticks) return;
			lastBackupTicks = now.Ticks;
			var dir = Path.GetDirectoryName(path);
			if (string.IsNullOrEmpty(dir)) return;
			var bdir = Path.Combine(dir!, "backup");
			Directory.CreateDirectory(bdir);
			File.Copy(path, Path.Combine(bdir, "inpa-data-" + now.ToString("yyyyMMdd-HHmmss") + ".json"), true);
			var files = new DirectoryInfo(bdir).GetFiles("inpa-data-*.json");
			if (files.Length > 30)
			{
				foreach (var f2 in files.OrderBy(x => x.LastWriteTimeUtc).Take(files.Length - 30))
				{
					try { f2.Delete(); } catch { }
				}
			}
		}
		catch (Exception e)
		{
			Log.Warning(e, "INPA Bridge: 备份数据文件失败");
		}
	}
	private static Task<T> RunOnFramework<T>(Func<T> fn)
	{
		TaskCompletionSource<T> tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		Framework.RunOnFrameworkThread((System.Action)delegate
		{
			try
			{
				tcs.TrySetResult(fn());
			}
			catch (Exception exception)
			{
				tcs.TrySetException(exception);
			}
		});
		return tcs.Task;
	}
}



