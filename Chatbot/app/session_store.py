from __future__ import annotations

import json
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from .config import get_settings

_SAFE_ID_RE = re.compile(r"[^a-zA-Z0-9_\-]")


def _safe_session_id(session_id: str) -> str:
    cleaned = _SAFE_ID_RE.sub("_", (session_id or "").strip())
    return cleaned[:120] or "anonymous"


def _session_file(session_id: str) -> Path:
    settings = get_settings()
    return settings.session_path / f"{_safe_session_id(session_id)}.json"


def load_history(session_id: str | None) -> list[dict[str, str]]:
    if not session_id:
        return []
    path = _session_file(session_id)
    if not path.exists():
        return []
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        if isinstance(data, dict) and isinstance(data.get("messages"), list):
            msgs = []
            for m in data["messages"]:
                if isinstance(m, dict) and "role" in m and "content" in m:
                    msgs.append({"role": str(m["role"]), "content": str(m["content"])})
            return msgs
    except Exception:
        return []
    return []


def save_turn(session_id: str | None, user_message: str, assistant_message: str, meta: dict[str, Any] | None = None) -> None:
    if not session_id:
        return
    try:
        settings = get_settings()
        path = _session_file(session_id)
        history = load_history(session_id)
        history.append({"role": "user", "content": user_message or ""})
        history.append({"role": "assistant", "content": assistant_message or ""})
        # Giới hạn lịch sử lưu trữ
        max_msgs = max(4, settings.max_history_messages * 2)
        if len(history) > max_msgs:
            history = history[-max_msgs:]
        payload = {
            "session_id": session_id,
            "updated_at_utc": datetime.now(timezone.utc).isoformat(),
            "messages": history,
            "meta": meta or {},
        }
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    except Exception:
        # Session history là best-effort, không được làm gián đoạn phản hồi
        return


def recent_history_text(session_id: str | None, limit: int = 8) -> str:
    msgs = load_history(session_id)
    if not msgs:
        return "(Chưa có lịch sử hội thoại trước đó.)"
    tail = msgs[-limit:]
    lines = []
    for m in tail:
        role = "Người dùng" if m.get("role") == "user" else "Trợ lý"
        content = (m.get("content") or "").strip().replace("\n", " ")
        if len(content) > 400:
            content = content[:400] + "..."
        lines.append(f"- {role}: {content}")
    return "\n".join(lines)
