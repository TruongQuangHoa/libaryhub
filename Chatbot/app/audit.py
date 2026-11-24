from __future__ import annotations

import json
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from .config import get_settings
from .schemas import AIUsageEvent, AuditEvent


def _utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def new_audit_id() -> str:
    return uuid.uuid4().hex


def _append_jsonl(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as f:
        f.write(json.dumps(payload, ensure_ascii=False, default=str) + "\n")


def log_audit_event(
    *,
    audit_id: str | None = None,
    event_type: str,
    role: str,
    user_id: int | None = None,
    user_name: str | None = None,
    session_id: str | None = None,
    allowed: bool,
    reason: str | None = None,
    intent: str | None = None,
    risk_level: str | None = None,
    input_text: str | None = None,
    output_text: str | None = None,
    metadata: dict[str, Any] | None = None,
) -> str:
    aid = audit_id or new_audit_id()
    try:
        settings = get_settings()
        event = AuditEvent(
            audit_id=aid,
            timestamp_utc=_utc_now_iso(),
            event_type=event_type,
            role=role,
            user_id=user_id,
            user_name=user_name,
            session_id=session_id,
            allowed=allowed,
            reason=reason,
            intent=intent,
            risk_level=risk_level,
            input_preview=(input_text or "")[:300] or None,
            output_preview=(output_text or "")[:300] or None,
            metadata=metadata or {},
        )
        log_file = settings.audit_path / f"audit-{datetime.now(timezone.utc):%Y%m%d}.jsonl"
        _append_jsonl(log_file, event.model_dump())
    except Exception:
        # Audit phải là best-effort, không được làm gián đoạn phản hồi người dùng
        pass
    return aid


def log_ai_usage(
    *,
    audit_id: str,
    model: str | None,
    prompt_chars: int,
    response_chars: int,
    success: bool,
    latency_ms: int | None = None,
    error: str | None = None,
) -> None:
    try:
        settings = get_settings()
        event = AIUsageEvent(
            audit_id=audit_id,
            timestamp_utc=_utc_now_iso(),
            model=model,
            prompt_chars=prompt_chars,
            response_chars=response_chars,
            success=success,
            latency_ms=latency_ms,
            error=(error or "")[:500] or None,
        )
        log_file = settings.audit_path / f"ai-usage-{datetime.now(timezone.utc):%Y%m%d}.jsonl"
        _append_jsonl(log_file, event.model_dump())
    except Exception:
        pass
