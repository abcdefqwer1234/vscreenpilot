using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VScreenPilot.Infrastructure;
using VScreenPilot.Json;
using VScreenPilot.Perception;
using VScreenPilot.Session;
using VScreenPilot.Views;
using static VScreenPilot.Mcp.JsonArgReader;

namespace VScreenPilot.Mcp;

/// <summary>22 个工具的元数据、输入/输出 schema 与策略声明。</summary>
internal sealed partial class McpTransport
{
    internal ToolRegistry BuildTools()
    {
        var tools = new ToolRegistry();

        Add(tools, "get_environment", "Environment",
            "Read virtual display, display topology, foreground window and session state. " +
            "foreground is only reported for tracked windows. ensure=true mounts/heals the virtual display first.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "ensure":{"type":"boolean","description":"Mount or heal the virtual display before reading state."}}}
            """,
            Out("virtual_display:object", "session:object", "foreground:object", "primary:object", "virtual_screen:object",
                "client_id:string", "screen_lease:object"),
            policy: new(Lease: ScreenLeaseMode.EnsureOption), readOnly: false, destructive: false, timeoutMs: 20000, GetEnvironment);

        Add(tools, "launch_or_place_app", "Launch or attach app",
            "Start an application or attach to an existing window, move it onto the virtual display and maximize it. " +
            "Returns pid/hwnd plus any owned dialogs discovered at attach time. Prefer path for a new instance; " +
            "window only when you intentionally take over a running app. Single-instance apps may fall back to " +
            "adopting an existing window: check `reused` and `warnings`; an adopted window is only moved back on " +
            "release, never closed. Windows 11 Notepad can restore the previous session's tabs when launched " +
            "without args; pass `args` or confirm the title with snapshot before typing.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "path":{"type":"string","description":"Executable path."},
              "window":{"type":"string","description":"Attach by window title. Ignores new_instance."},
              "args":{"type":"string"},
              "wait_ms":{"type":"integer","minimum":1000,"maximum":60000},
              "new_instance":{"type":"boolean","description":"Default true: dedicated/new window."},
              "ensure_document":{"type":"boolean","description":"Default true. Word: Ctrl+N. PowerPoint: Ctrl+N then the blank-presentation tile. PowerPoint/Excel report document_ready_reason; apps without modeling return document_ready=null."},
              "pid":{"type":"integer","minimum":1,"description":"Attach by process id (most precise)."}}}
            """,
            Out("launched:boolean", "new_instance:boolean", "reused:boolean", "pid:integer", "hwnd:integer",
                "title:string", "maximized:boolean", "document_ready:boolean",
                "document_ready_reason:string", "dialogs:array", "warnings:array"),
            policy: new(Mutates: true), readOnly: false, destructive: false, timeoutMs: 90000, LaunchOrPlace);

        Add(tools, "snapshot", "Snapshot",
            "One-call observation: compact semantic tree with refs, tracked windows, owned dialogs, focus and " +
            "optional screenshot. Each call advances the ref generation: refs from an older snapshot return " +
            "E_STALE_SNAPSHOT. (find_element/get_ui_tree reuse the current generation.)",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "pid":{"type":"integer","minimum":1},
              "max_depth":{"type":"integer","minimum":1,"maximum":10,"description":"Default 4."},
              "interactive_only":{"type":"boolean","description":"Default true."},
              "max_nodes":{"type":"integer","minimum":1,"maximum":1024,"description":"Default 300; max 1024 (ref capacity)."},
              "include_screenshot":{"type":"boolean","description":"Default false; adds a scaled PNG."},
              "annotate":{"type":"boolean","description":"Default false; draw element boxes and numbered labels on the screenshot. Implies include_screenshot."},
              "scale":{"type":"number","minimum":0.1,"maximum":1,"description":"Screenshot scale, default 0.5."},
              "include_values":{"type":"boolean","description":"Include text/value of text controls. Default true."}}}
            """,
            Out("snapshot_id:string", "tree:object", "text:string", "nodes:integer",
                "truncated:boolean", "max_nodes_effective:integer", "ref_capacity:integer",
                "windows:array", "dialogs:array", "foreground:object", "screenshot:object",
                "screenshot_path:string", "screenshot_uri:string", "screenshot_preview:object", "annotations:array"),
            policy: new(Lease: ScreenLeaseMode.SnapshotOptions), readOnly: false, destructive: false, timeoutMs: 45000, Snapshot);

        Add(tools, "capture_screen", "Capture screen",
            "Capture the virtual display or a region. Returns a PNG plus meta. If save_path is given it is the only " +
            "output file; otherwise large frames are written to the session artifact directory and returned as a " +
            "resource link. inline=true (default) embeds small images and a 0.25x preview for large ones. " +
            "save_path must be an absolute .png under the working directory or temp.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "region":{"type":"object","additionalProperties":false,"properties":{
                "x":{"type":"integer"},"y":{"type":"integer"},
                "width":{"type":"integer","minimum":1},"height":{"type":"integer","minimum":1}},
                "required":["x","y","width","height"]},
              "scale":{"type":"number","minimum":0.1,"maximum":1,"description":"Default 0.5 for full screen, 1 for regions."},
              "inline":{"type":"boolean","description":"Embed the PNG in the MCP result. Default true for small images."},
              "save_path":{"type":"string","description":"Absolute .png path under cwd or temp."}}}
            """,
            Out("frame_id:string", "x:integer", "y:integer", "width:integer", "height:integer",
                "source_width:integer", "source_height:integer", "scale:number", "png_bytes:integer",
                "duration_ms:number", "encode_ms:number", "path:string",
                "artifact_path:string", "resource_uri:string", "artifact_uri:string",
                "inline:boolean", "preview:object", "preview_error:string",
                "backend:string", "slow_capture:boolean"),
            policy: new(), readOnly: false, destructive: false, timeoutMs: 30000, CaptureScreen);

        Add(tools, "find_element", "Find element",
            "Find UI Automation elements and return refs, bounds, state and snapshot_id. Falls back to MSAA when a " +
            "window exposes no UIA content (Office legacy dialogs). control_type must be a valid UIA control type. " +
            "role=document_body maps the document text surface.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "name":{"type":"string"},
              "name_match":{"type":"string","enum":["exact","contains","regex"]},
              "control_type":{"type":"string","description":"UIA control type, e.g. Button, Edit, Document."},
              "automation_id":{"type":"string"},
              "role":{"type":"string","enum":["document","document_body"]},
              "prefer":{"type":"string","enum":["auto","first","focused","largest","last"]},
              "index":{"type":"integer","minimum":0},
              "all":{"type":"boolean"},
              "max_results":{"type":"integer","minimum":1,"maximum":200},
              "include_offscreen":{"type":"boolean"},
              "pid":{"type":"integer","minimum":1}}}
            """,
            Out("ref:string", "count:integer", "total:integer", "truncated:boolean",
                "snapshot_id:string", "name:string", "control_type:string", "bounds:object",
                "enabled:boolean", "offscreen:boolean", "matches:array", "elements:array"),
            policy: new(Lease: ScreenLeaseMode.None), readOnly: true, destructive: false, timeoutMs: 30000, FindElement);

        Add(tools, "get_ui_tree", "UI tree",
            "Read the pruned semantic tree of the tracked window. Nodes carry refs. By default it reuses the current " +
            "snapshot generation, so existing refs stay valid; pass new_snapshot=true to invalidate old refs.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "max_depth":{"type":"integer","minimum":1,"maximum":10},
              "interactive_only":{"type":"boolean"},
              "max_nodes":{"type":"integer","minimum":1,"maximum":1024},
              "root":{"type":"string"},
              "pid":{"type":"integer","minimum":1},
              "format":{"type":"string","enum":["json","text"],"description":"text is the compact line format."},
              "new_snapshot":{"type":"boolean","description":"Advance the ref generation. Default false: keep existing refs valid."}}}
            """,
            Out("tree:object", "text:string", "nodes:integer", "truncated:boolean",
                "truncated_reason:string", "max_nodes_effective:integer",
                "max_nodes_clamped:boolean", "ref_capacity:integer", "snapshot_id:string",
                "new_snapshot:boolean"),
            policy: new(Lease: ScreenLeaseMode.None), readOnly: true, destructive: false, timeoutMs: 30000, GetUiTree);

        Add(tools, "invoke_element", "Invoke element",
            "Invoke a control by ref. auto tries Invoke/Toggle/SelectionItem/ExpandCollapse/Legacy; pattern=default " +
            "focuses and presses Space/Enter (useful for tree nodes).",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "ref":{"type":"string"},
              "pattern":{"type":"string","enum":["auto","invoke","toggle","select","selectionitem","expand","expandcollapse","legacy","default","defaultaction","open"]},
              "allow_click":{"type":"boolean","description":"Allow message click fallback."}},
              "required":["ref"]}
            """,
            Out("invoked:boolean", "pattern:string", "channel:string", "verified:boolean"),
            policy: new(Mutates: true), readOnly: false, destructive: true, timeoutMs: 30000, InvokeElement);

        Add(tools, "set_element_value", "Set value",
            "Write a value by ref. ValuePattern first; allow_keyboard=true falls back to typing. Overwriting non-empty " +
            "content requires allow_destructive=true.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "ref":{"type":"string"},
              "value":{"type":"string"},
              "allow_keyboard":{"type":"boolean"},
              "mode":{"type":"string","enum":["auto","append","insert","replace"]},
              "allow_destructive":{"type":"boolean"}},
              "required":["ref","value"]}
            """,
            Out("written:string", "value:string", "mode:string", "verified:boolean", "observed_length:integer"),
            policy: new(Mutates: true), readOnly: false, destructive: true, timeoutMs: 30000, SetElementValue);

        Add(tools, "get_element_text", "Get text",
            "Read text through TextPattern then ValuePattern. Use it to verify what you typed.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "ref":{"type":"string"},
              "max_chars":{"type":"integer","minimum":1,"maximum":65536}},
              "required":["ref"]}
            """,
            Out("text:string", "length:integer", "source:string", "truncated:boolean"),
            policy: new(Lease: ScreenLeaseMode.None), readOnly: true, destructive: false, timeoutMs: 20000, GetElementText);

        Add(tools, "click", "Click",
            "Click at virtual-desktop coordinates. channel=auto tries a UIA element, then a window message; " +
            "it only falls back to SendInput when fallback_input=true (or VSP_INPUT_AUTOFALLBACK=1). " +
            "Non-client areas (title bar, caption buttons) and popup menus are handled explicitly. " +
            "keep_foreground=true pauses the focus guard for 30s.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "x":{"type":"integer"},"y":{"type":"integer"},
              "button":{"type":"string","enum":["left","right","middle"]},
              "clicks":{"type":"integer","minimum":1,"maximum":10},
              "mode":{"type":"string","enum":["auto","uia","message","input"],"description":"Legacy alias of channel."},
              "channel":{"type":"string","enum":["auto","uia","message","input"]},
              "keep_foreground":{"type":"boolean"},
              "fallback_input":{"type":"boolean","description":"Default false. When UIA/message paths fail, move the real cursor and use SendInput."},
              "observe":{"type":"boolean","description":"Default true: capture a small before/after diff and return observed.changed."}},
              "required":["x","y"]}
            """,
            Out("x:integer", "y:integer", "button:string", "clicks:integer", "mode:string",
                "method:string", "inject_ms:number", "duration_ms:number", "verified:boolean",
                "keep_foreground:boolean", "detail:string", "hint:string", "observed:object"),
            policy: new(Mutates: true), readOnly: false, destructive: true, timeoutMs: 25000, Click);

        Add(tools, "click_ref", "Click element by ref",
            "Click a control by element_ref. auto first tries UIA Invoke/Toggle/Select, then message click " +
            "at the control center; channel=input moves the real cursor. fallback_input=true (or " +
            "VSP_INPUT_AUTOFALLBACK=1) enables SendInput after UIA/message fail. Prefer this over coordinate clicks.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "ref":{"type":"string"},
              "channel":{"type":"string","enum":["auto","uia","message","input"]},
              "keep_foreground":{"type":"boolean"},
              "fallback_input":{"type":"boolean","description":"Default false. When UIA/message paths fail, move the real cursor and use SendInput."}},
              "required":["ref"]}
            """,
            Out("clicked:boolean", "ref:string", "channel:string", "pattern:string",
                "refreshed:boolean", "bounds:object", "observed:object"),
            policy: new(Mutates: true), readOnly: false, destructive: true, timeoutMs: 20000, ClickRef);

        Add(tools, "read_screen_text", "Read screen text (OCR)",
            "OCR the virtual display or a region with the built-in Windows OCR engine. Use it for canvas apps " +
            "(PowerPoint, charts, games) where UIA exposes no text. Optional query filters lines by substring.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "region":{"type":"object","additionalProperties":false,"properties":{
                "x":{"type":"integer"},"y":{"type":"integer"},
                "width":{"type":"integer","minimum":1},"height":{"type":"integer","minimum":1}},
                "required":["x","y","width","height"]},
              "query":{"type":"string"},
              "max_lines":{"type":"integer","minimum":1,"maximum":200}}}
            """,
            Out("engine:string", "language:string", "lines:array", "matches:array", "count:integer",
                "returned:integer", "truncated:boolean", "region:object", "duration_ms:number"),
            policy: new(), readOnly: true, destructive: false, timeoutMs: 60000, ReadScreenText);

        Add(tools, "click_text", "Click text on screen",
            "OCR the virtual display (or a region), find the first line containing text, click its center and " +
            "return the before/after diff. Fallback for canvas apps where click_ref cannot find the element. " +
            "fallback_input=true (or VSP_INPUT_AUTOFALLBACK=1) enables SendInput after UIA/message fail.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "text":{"type":"string"},
              "region":{"type":"object","additionalProperties":false,"properties":{
                "x":{"type":"integer"},"y":{"type":"integer"},
                "width":{"type":"integer","minimum":1},"height":{"type":"integer","minimum":1}},
                "required":["x","y","width","height"]},
              "channel":{"type":"string","enum":["auto","uia","message","input"]},
              "keep_foreground":{"type":"boolean"},
              "fallback_input":{"type":"boolean","description":"Default false. When UIA/message paths fail, move the real cursor and use SendInput."},
              "require_unique":{"type":"boolean","description":"Fail with E_MULTIPLE_MATCHES when more than one line matches."},
              "observe":{"type":"boolean"}},
              "required":["text"]}
            """,
            Out("found:boolean", "text:string", "x:integer", "y:integer", "bounds:object",
                "ambiguous:boolean", "matches:array", "method:string", "observed:object"),
            policy: new(Mutates: true), readOnly: false, destructive: true, timeoutMs: 60000, ClickText);

        Add(tools, "gesture", "Gesture",
            "Composite mouse action: drag, scroll or move. Message mode cannot drag across windows; use " +
            "mode=input for drags (this tool uses `mode`, not `channel`).",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "type":{"type":"string","enum":["drag","scroll","move"]},
              "from":{"type":"object","additionalProperties":false,"properties":{"x":{"type":"integer"},"y":{"type":"integer"}},"required":["x","y"]},
              "to":{"type":"object","additionalProperties":false,"properties":{"x":{"type":"integer"},"y":{"type":"integer"}},"required":["x","y"]},
              "dx":{"type":"integer"},"dy":{"type":"integer"},
              "button":{"type":"string","enum":["left","right","middle"]},
              "duration_ms":{"type":"integer","minimum":20,"maximum":5000},
              "observe":{"type":"boolean","description":"Default true: capture a before/after diff and return observed.changed."},
              "mode":{"type":"string","enum":["auto","message","input"]}},
              "required":["type"]}
            """,
            Out("type:string", "from:object", "to:object", "dx:integer", "dy:integer",
                "button:string", "method:string", "detail:string", "verified:boolean",
                "inject_ms:number", "duration_ms:number", "observed:object", "hint:string"),
            policy: new(Mutates: true), readOnly: false, destructive: true, timeoutMs: 35000, Gesture);

        Add(tools, "type_text", "Type text",
            "Type text into the focused control. auto uses a pattern write, clipboard paste for long text, or " +
            "SendInput as fallback; verify=true reads back through TextPattern/ValuePattern.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "text":{"type":"string"},
              "pid":{"type":"integer","minimum":1},
              "verify":{"type":"boolean"},
              "chunk_chars":{"type":"integer","minimum":1,"maximum":256,"description":"Chunk size in characters; default 32."},
              "channel":{"type":"string","enum":["auto","pattern","clipboard","keys"]}},
              "required":["text"]}
            """,
            Out("typed:integer", "verified:boolean", "channel:string", "inject_ms:number", "duration_ms:number",
                "observed_length:integer", "chunks:integer"),
            policy: new(Mutates: true), readOnly: false, destructive: true, timeoutMs: 120000, TypeText);

        Add(tools, "press_keys", "Press keys",
            "Send a key combination or a sequence inside one foreground lease. System shortcuts (Win+*, Alt+Tab, " +
            "Ctrl+Esc and variants) are rejected unless allow_system_keys=true.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "keys":{"type":"string"},
              "sequence":{"type":"array","items":{"type":"string"}},
              "keep_foreground":{"type":"boolean"},
              "allow_system_keys":{"type":"boolean"},
              "observe":{"type":"boolean","description":"Default true: capture a before/after diff and return observed.changed."},
              "pid":{"type":"integer","minimum":1}}}
            """,
            Out("keys:string", "count:integer", "target_pid:integer", "inject_ms:number",
                "duration_ms:number", "keep_foreground:boolean", "verified:boolean", "observed:object"),
            policy: new(Mutates: true), readOnly: false, destructive: true, timeoutMs: 35000, PressKeys);

        Add(tools, "wait_for", "Wait for condition",
            "Poll a condition on the server inside one call. type=element|color|window|pixel_diff|visual|text. " +
            "Read-only: it never holds the write lock, so other observations keep working.",
            """
            {"type":"object","required":["condition"],"additionalProperties":false,"properties":{
              "timeout_ms":{"type":"integer","minimum":0,"maximum":120000},
              "interval_ms":{"type":"integer","minimum":50,"maximum":2000},
              "condition":{"oneOf":[
                {"type":"object","required":["type"],"properties":{"type":{"const":"element"},"name":{"type":"string"},"control_type":{"type":"string"},"automation_id":{"type":"string"},"pid":{"type":"integer"}}},
                {"type":"object","required":["type","x","y","value"],"properties":{"type":{"const":"color"},"x":{"type":"integer"},"y":{"type":"integer"},"value":{"type":"string"},"tolerance":{"type":"integer","minimum":0,"maximum":255}}},
                {"type":"object","required":["type"],"properties":{"type":{"const":"window"},"title":{"type":"string"},"scope":{"type":"string","enum":["tracked","any"]}}},
                {"type":"object","required":["type","region"],"properties":{"type":{"const":"pixel_diff"},"region":{"type":"object"},"baseline_frame_id":{"type":"string"},"min_changed_ratio":{"type":"number","minimum":0,"maximum":1}}},
                {"type":"object","required":["type"],"properties":{"type":{"const":"visual"},"mode":{"type":"string"},"target":{},"region":{"type":"object"},"threshold":{"type":"number"}}},
                {"type":"object","required":["type","text"],"properties":{"type":{"const":"text"},"text":{"type":"string"},"region":{"type":"object"}}}
              ]}}}
            """,
            Out("satisfied:boolean", "elapsed_ms:integer", "last:object"),
            policy: new(Lease: ScreenLeaseMode.WaitCondition, SessionRead: false), readOnly: true, destructive: false, timeoutMs: 130000, WaitFor);

        Add(tools, "assert_region", "Assert region",
            "Assert color coverage or change against a baseline. An explicit baseline_frame_id must match x/y/width/height " +
            "exactly; mismatch returns E_NO_BASELINE instead of comparing different areas.",
            """
            {"type":"object","required":["region","expectation"],"additionalProperties":false,"properties":{
              "region":{"type":"object","additionalProperties":false,"properties":{"x":{"type":"integer"},"y":{"type":"integer"},"width":{"type":"integer","minimum":1},"height":{"type":"integer","minimum":1}},"required":["x","y","width","height"]},
              "expectation":{"oneOf":[
                {"type":"object","required":["color"],"properties":{"color":{"type":"string"},"coverage":{"type":"number","minimum":0,"maximum":1},"tolerance":{"type":"integer","minimum":0,"maximum":255}}},
                {"type":"object","required":["changed"],"properties":{"changed":{"type":"boolean"},"threshold":{"type":"number","minimum":0,"maximum":1},"baseline_frame_id":{"type":"string"},"baseline":{"type":"string","enum":["last_same_region","last"]}}}
              ]}}}
            """,
            Out("passed:boolean", "changed_ratio:number", "coverage:number", "baseline_frame_id:string"),
            policy: new(), readOnly: true, destructive: false, timeoutMs: 30000, AssertRegion);

        Add(tools, "match_visual", "Match visual",
            "Locate pixels on the virtual display: color connected components, single pixel, or PNG template match. " +
            "Template matching is done at capture pixel scale; if the template comes from a different render " +
            "scale/DPI, pass scales (0.5-2.0, default [1], max 5) to search.",
            """
            {"type":"object","required":["mode"],"additionalProperties":false,"properties":{
              "mode":{"type":"string","enum":["template","color","pixel"]},
              "target":{"description":"color: #RRGGBB; pixel: {x,y}; template: base64 PNG"},
              "target_path":{"type":"string"},
              "region":{"type":"object","additionalProperties":false,"properties":{"x":{"type":"integer"},"y":{"type":"integer"},"width":{"type":"integer","minimum":1},"height":{"type":"integer","minimum":1}},"required":["x","y","width","height"]},
              "tolerance":{"type":"integer","minimum":0,"maximum":255},
              "threshold":{"type":"number","minimum":0.3,"maximum":1},
              "scales":{"type":"array","items":{"type":"number","minimum":0.5,"maximum":2},"maxItems":5,
                "description":"Template scales to try, e.g. [0.75,1,1.25]; default [1]."},
              "max_results":{"type":"integer","minimum":1,"maximum":100},
              "min_pixels":{"type":"integer","minimum":1},
              "max_area_ratio":{"type":"number","minimum":0.01,"maximum":1},
              "exclude_border":{"type":"boolean"},
              "require_unique":{"type":"boolean"}}}
            """,
            Out("found:boolean", "count:integer", "x:integer", "y:integer", "width:integer", "height:integer",
                "color:string", "matches:array", "threshold:number", "template:object", "scale:number"),
            policy: new(), readOnly: true, destructive: false, timeoutMs: 60000, MatchVisual);

        Add(tools, "release_environment", "Release environment",
            "End the debug session: restore window placement, close only processes this session started, optionally " +
            "remove the virtual display. on_dirty controls unsaved content. Default is abort: unsaved windows are kept " +
            "and the display stays mounted. force/discard require allow_data_loss=true.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "keep_display":{"type":"boolean","description":"Default true."},
              "on_dirty":{"type":"string","enum":["force","discard","save","abort"],"description":"Default abort."},
              "allow_data_loss":{"type":"boolean","description":"Required for on_dirty=force/discard when a tracked app may have unsaved content."}}}
            """,
            Out("released:boolean", "display_removed:boolean", "display_kept:boolean",
                "on_dirty:string", "restored_pids:array", "closed_pids:array",
                "forced_pids:array", "kept_pids:array", "saved_pids:array",
                "discarded_pids:array", "memory:object", "warnings:array"),
            policy: new(Mutates: true, Lease: ScreenLeaseMode.IfLiveApps, SessionWrite: true, BypassWriteLock: true, WriteTimeoutMs: 6000), readOnly: false, destructive: true, timeoutMs: 90000, ReleaseEnvironment);

        Add(tools, "get_log", "Get log",
            "Read recent in-memory or file log entries, filtered by level/contains. Use it after E_INTERNAL or E_TIMEOUT.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "tail":{"type":"integer","minimum":1,"maximum":500},
              "source":{"type":"string","enum":["memory","file","both"]},
              "level":{"type":"string","enum":["DEBUG","INFO","WARN","ERROR"]},
              "contains":{"type":"string"},
              "include_other_clients":{"type":"boolean","description":"Include entries owned by other MCP clients. Default false."}}}
            """,
            Out("source:string", "entries:array", "file_entries:array", "last_error:object"),
            policy: new(Lease: ScreenLeaseMode.None, SessionRead: false), readOnly: true, destructive: false, timeoutMs: 15000, GetLog);

        Add(tools, "run_script", "Run script",
            "Run a temporary PowerShell script with the current user token. Disabled unless the server was started " +
            "with --allow-script. Output is UTF-8 and truncated to 64KB.",
            """
            {"type":"object","required":["code"],"additionalProperties":false,"properties":{
              "code":{"type":"string"},
              "timeout_ms":{"type":"integer","minimum":1000,"maximum":120000}}}
            """,
            Out("stdout:string", "stderr:string", "exit_code:integer", "duration_ms:number",
                "timed_out:boolean", "truncated:boolean"),
            policy: new(Mutates: true), readOnly: false, destructive: true, timeoutMs: 130000, RunScript);

        return tools;
    }

    private static void Add(
        ToolRegistry tools,
        string name,
        string title,
        string description,
        string schemaJson,
        JsonNode outputSchema,
        ToolPolicy policy,
        bool readOnly,
        bool destructive,
        int timeoutMs,
        Func<JsonElement, ToolResponse> handler)
    {
        tools.Add(new ToolDescriptor(
            name, title, description,
            JsonNode.Parse(schemaJson)!, outputSchema,
            readOnly, destructive, timeoutMs, policy, handler));
    }

    /// <summary>生成“成功或错误都能通过”的输出 schema：声明字段但不强制 required。</summary>
    private static JsonNode Out(params string[] fields)
    {
        var properties = new JsonObject
        {
            ["code"] = Type("string"),
            ["message"] = Type("string"),
        };

        foreach (string field in fields)
        {
            int separator = field.IndexOf(':');

            if (separator <= 0)
            {
                continue;
            }

            properties[field[..separator]] = Type(field[(separator + 1)..]);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = true,
        };
    }

    private static JsonObject Type(string type) => type switch
    {
        // 输出字段允许 null（例如 last_error、path），schema 仍声明真实类型。
        "object" => new JsonObject
        {
            ["type"] = new JsonArray("object", "null"),
            ["additionalProperties"] = true,
        },
        "array" => new JsonObject
        {
            ["type"] = new JsonArray("array", "null"),
            ["items"] = new JsonObject(),
        },
        _ => new JsonObject { ["type"] = new JsonArray(type, "null") },
    };

}
