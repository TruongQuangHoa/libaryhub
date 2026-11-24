from __future__ import annotations

from typing import Any

from .schemas import ChatResponse, ExportInfo


def build_response(
    *,
    success: bool,
    message: str,
    intent: str | None = None,
    risk_level: str | None = None,
    requires_human_approval: bool = False,
    checkpoint_id: str | None = None,
    audit_id: str | None = None,
    export: dict[str, str] | None = None,
    sources: list[str] | None = None,
    debug: dict[str, Any] | None = None,
) -> ChatResponse:
    export_info = ExportInfo(**export) if export else None
    return ChatResponse(
        success=success,
        message=message,
        intent=intent,
        risk_level=risk_level,
        requires_human_approval=requires_human_approval,
        checkpoint_id=checkpoint_id,
        audit_id=audit_id,
        export=export_info,
        sources=sources or [],
        debug=debug or {},
    )


def format_markdown_answer(
    answer: str,
    *,
    export: dict[str, str] | None = None,
    checkpoint_id: str | None = None,
    requires_human_approval: bool = False,
) -> str:
    parts = [answer.strip()]
    if export:
        parts.append(
            "\n---\n"
            f"**Tệp báo cáo đã tạo ({export.get('type','').upper()}):** "
            f"[{export.get('filename','download')}]({export.get('url','')})"
        )
    if requires_human_approval and checkpoint_id:
        parts.append(
            "\n> **Yêu cầu xác nhận human-in-the-loop:** vui lòng gửi lại `checkpoint_id` và `approval_token` "
            "đã được cấp để tiếp tục quy trình kiểm soát."
        )
    return "\n".join(p for p in parts if p).strip()
