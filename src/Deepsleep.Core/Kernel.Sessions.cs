using System;
using System.Collections.Generic;
using System.Linq;

namespace TrollWrangler.Core;

public sealed partial class Kernel
{
    /// <summary>推 Session 索引（可按 kind / state 过滤）。前端与 Router、Watchdog 共用这一份。</summary>
    private void EmitSessions(string? kind, string? state)
    {
        var list = Sessions.List(
            string.IsNullOrWhiteSpace(kind) ? null : kind,
            string.IsNullOrWhiteSpace(state) ? null : state);
        Emit(new
        {
            ev = "sessions",
            items = list.Select(e => (object)new
            {
                sid = e.Sid,
                kind = e.Kind,
                parentSid = e.ParentSid,
                title = e.Title,
                state = e.State,
                messageCount = e.MessageCount,
                createdUtc = e.CreatedUtc.ToString("O"),
                updatedUtc = e.UpdatedUtc.ToString("O"),
                summary = e.Summary,
            }).ToList(),
        });
    }

    /// <summary>推单个 Session 的完整消息，供"查看会话上下文"用。</summary>
    private void EmitSession(int sid)
    {
        var r = Sessions.Load(sid);
        if (r == null)
        {
            Emit(new { ev = "session", sid, found = false });
            return;
        }
        Emit(new
        {
            ev = "session",
            sid,
            found = true,
            record = new
            {
                sid = r.Sid,
                kind = r.Kind,
                parentSid = r.ParentSid,
                title = r.Title,
                state = r.State,
                summary = r.Summary,
                createdUtc = r.CreatedUtc.ToString("O"),
                updatedUtc = r.UpdatedUtc.ToString("O"),
                messages = r.Messages.Select(m => (object)new
                {
                    role = m.Role, content = m.Content, meta = m.Meta,
                    imagePath = m.ImagePath, videoPath = m.VideoPath,
                }).ToList(),
            },
        });
    }
}
