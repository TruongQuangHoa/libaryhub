from __future__ import annotations

from enum import Enum
from typing import Any, Literal, Optional

from pydantic import BaseModel, Field, field_validator


class UserRole(str, Enum):
    Patron = "Patron"
    Librarian = "Librarian"
    Admin = "Admin"
    Guest = "Guest"


class RiskLevel(str, Enum):
    low = "low"
    medium = "medium"
    high = "high"


class IntentKind(str, Enum):
    read_lookup = "read_lookup"
    missing_context = "missing_context"
    sensitive_action = "sensitive_action"
    clarification_needed = "clarification_needed"


class ChatRequest(BaseModel):
    message: str = Field(..., min_length=1, max_length=4000)
    role: UserRole = UserRole.Guest
    user_id: Optional[int] = None
    user_name: Optional[str] = None
    session_id: Optional[str] = None
    conversation_id: Optional[str] = None
    page_context: Optional[str] = Field(
        default=None,
        description="Ngữ cảnh trang con/giao diện hiện tại (Patron/Librarian/Admin).",
    )
    metadata: dict[str, Any] = Field(default_factory=dict)
    # Hỗ trợ luồng human-in-the-loop: client gửi lại approval/checkpoint token nếu có
    approval_token: Optional[str] = None
    checkpoint_id: Optional[str] = None
    requested_export: Optional[Literal["xlsx", "docx", "pdf"]] = None

    @field_validator("message")
    @classmethod
    def validate_message_not_blank(cls, v: str) -> str:
        if not str(v or "").strip():
            raise ValueError("message must not be blank")
        return str(v).strip()


class ExportInfo(BaseModel):
    type: str
    filename: str
    url: str


class ChatResponse(BaseModel):
    success: bool
    message: str
    intent: Optional[str] = None
    risk_level: Optional[str] = None
    requires_human_approval: bool = False
    checkpoint_id: Optional[str] = None
    audit_id: Optional[str] = None
    export: Optional[ExportInfo] = None
    sources: list[str] = Field(default_factory=list)
    debug: dict[str, Any] = Field(default_factory=dict)


class AuditEvent(BaseModel):
    audit_id: str
    timestamp_utc: str
    event_type: str
    role: str
    user_id: Optional[int] = None
    user_name: Optional[str] = None
    session_id: Optional[str] = None
    allowed: bool
    reason: Optional[str] = None
    intent: Optional[str] = None
    risk_level: Optional[str] = None
    input_preview: Optional[str] = None
    output_preview: Optional[str] = None
    metadata: dict[str, Any] = Field(default_factory=dict)


class AIUsageEvent(BaseModel):
    audit_id: str
    timestamp_utc: str
    provider: str = "gemini"
    model: Optional[str] = None
    prompt_chars: int = 0
    response_chars: int = 0
    success: bool = False
    latency_ms: Optional[int] = None
    error: Optional[str] = None
