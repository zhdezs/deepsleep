using System;
using System.Collections.Generic;
using System.Linq;

namespace TrollWrangler.Core;

/// <summary>会话运行状态（发送 / 停止 / 恢复 三态按钮用）。</summary>
public enum RunState { Idle, Running, Stopped }

/// <summary>
/// 一条聊天消息（纯数据，不含任何界面类型）。界面（HTML）只认这些字段，
/// 颜色 / 头像 / 显隐规则全部交给前端决定。
/// </summary>
public sealed class ChatItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public bool IsSelf { get; set; }
    public bool IsSys { get; set; }
    public string Text { get; set; } = "";
    public string Meta { get; set; } = "";
    public string TimeStr { get; set; } = "";
    public bool ShowTime { get; set; }
    public string AvatarText { get; set; } = "AI";
    public string SelfAvatarText { get; set; } = "你";
    /// <summary>头像底色（十六进制，前端直接用）。</summary>
    public string AvatarColor { get; set; } = "#4A90D9";
    /// <summary>「思考中 / 正在执行」占位气泡，前端做呼吸动效。</summary>
    public bool Thinking { get; set; }
    public double ThinkingOpacity { get; set; } = 1.0;
    /// <summary>附带的本地图片（生成图片 / 上传的图片）。</summary>
    public string? ImagePath { get; set; }
    public string AttachmentName { get; set; } = "";
    public string AttachmentSize { get; set; } = "";
    public bool CanAccept { get; set; } = true;
    public bool Accepted { get; set; }
    public int SessionId { get; set; }
    /// <summary>集群成员气泡：显示角色名（"Agent「前端开发」"）。</summary>
    public string Speaker { get; set; } = "";

    /// <summary>工具结果卡：工具名（网络搜索 / 深度研究 / 运行命令 / 写入文件…）。空 = 普通气泡。</summary>
    public string ToolName { get; set; } = "";
    /// <summary>折叠时显示的那一句话（「搜到 8 篇资料」/「命令：python a.py」）。</summary>
    public string ToolSummary { get; set; } = "";
    /// <summary>展开后的明细（搜索结果列表 / 命令输出 / 文件内容）。</summary>
    public string ToolDetail { get; set; } = "";
    /// <summary>工具大类：cmd（运行命令）/file（写文件）/search/research/vision/read/other。
    /// 界面把同一轮里连续的 cmd、file 各自合并成一张可展开卡片。</summary>
    public string ToolKind { get; set; } = "";

    /// <summary>模型的思考过程（reasoning）。界面把它折叠在正文上方（默认不展开），绝不混进正文。</summary>
    public string Thought { get; set; } = "";
}

/// <summary>一个对话会话（AI 助手 kind=0 / Agent 集群 kind=2）。</summary>
public sealed class Conversation
{
    public int Sid { get; set; }
    public int Kind { get; set; }
    public string Title { get; set; } = "";
    public string Created { get; set; } = "";
    public bool IsPinned { get; set; }
    public bool AutoApprove { get; set; }
    public List<ChatItem> Items { get; } = new();
}

/// <summary>Agent 集群里的一名成员。</summary>
public sealed class ClusterWorker
{
    public int Sid { get; set; }
    public string Name { get; set; } = "";
    public string Task { get; set; } = "";
    public string Status { get; set; } = "空闲";
    public string LogText { get; set; } = "";
    /// <summary>正在做什么（最近一次工具调用），实时推给界面上的「Agent 名片」。</summary>
    public string Activity { get; set; } = "";
    /// <summary>这一轮的产出（成员自己的回答），汇总时按人喂给指挥官。</summary>
    public string Result { get; set; } = "";
}
