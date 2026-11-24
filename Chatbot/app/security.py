from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Iterable

from fastapi import Header, HTTPException, Request

from .config import get_settings


@dataclass
class SecurityDecision:
    allowed: bool
    reason: str = "OK"
    risk_flags: list[str] | None = None


# Heuristic AgentShield patterns (phòng thủ lớp ứng dụng, không thay thế WAF/IDS)
_PROMPT_INJECTION_PATTERNS: list[tuple[re.Pattern[str], str]] = [
    (re.compile(r"ignore\s+(all\s+)?(previous|prior|above)\s+instructions", re.I), "prompt_injection_ignore_instructions"),
    (re.compile(r"ignore\s+all\s+instructions", re.I), "prompt_injection_ignore_instructions"),
    (re.compile(r"bỏ\s+qua\s+(mọi|tất\s*cả|các)\s+(chỉ\s*dẫn|hướng\s*dẫn|quy\s*tắc)", re.I), "prompt_injection_ignore_instructions_vi"),
    (re.compile(r"reveal\s+(system|developer)\s+(prompt|message)", re.I), "prompt_leak_attempt"),
    (re.compile(r"(tiết\s*lộ|in\s*ra|hiển\s*thị).*(system\s*prompt|chỉ\s*dẫn\s*hệ\s*thống)", re.I), "prompt_leak_attempt_vi"),
    (re.compile(r"you\s+are\s+now\s+(dan|developer|root|admin|system)", re.I), "role_override_attempt"),
    (re.compile(r"act\s+as\s+(system|developer|admin|root)", re.I), "role_override_attempt"),
    (re.compile(r"\b(api[_\s-]?key|secret|token|password|mật\s*khẩu)\b.*\b(show|give|list|hiển\s*thị|cho\s*tôi|reveal|print)\b", re.I), "secret_exfiltration_attempt"),
    (re.compile(r"\b(show|give|list|hiển\s*thị|cho\s*tôi|reveal|print)\b.*\b(api[_\s-]?key|secret|token|password|mật\s*khẩu)\b", re.I), "secret_exfiltration_attempt"),
    (re.compile(r"<\s*script\b|javascript:\s*|onerror\s*=", re.I), "xss_payload"),
    (re.compile(r"union\s+select|drop\s+table|;\s*--|xp_cmdshell", re.I), "sql_injection_pattern"),
]

_SENSITIVE_ACTION_HINTS: tuple[str, ...] = (
    "xóa", "xoá", "delete", "drop", "update", "sửa trực tiếp", "cập nhật csdl",
    "đổi cấu hình", "thay đổi cấu hình", "config", "lên lịch", "schedule",
    "chạy tác vụ", "execute", "deploy", "phân quyền", "grant role", "reset mật khẩu",
    "xóa tài khoản", "khóa tài khoản", "mở khóa", "cấp quyền", "thu hồi quyền",
)


def agentshield_scan(message: str) -> SecurityDecision:
    settings = get_settings()
    if not settings.enable_agentshield:
        return SecurityDecision(True, "AgentShield disabled")

    text = (message or "").strip()
    if not text:
        return SecurityDecision(False, "Nội dung yêu cầu trống.", ["empty_input"])
    if len(text) > settings.max_message_length:
        return SecurityDecision(
            False,
            f"Nội dung vượt quá giới hạn {settings.max_message_length} ký tự.",
            ["input_too_long"],
        )

    flags: list[str] = []
    for pattern, flag in _PROMPT_INJECTION_PATTERNS:
        if pattern.search(text):
            flags.append(flag)

    # Phát hiện yêu cầu nguy hiểm mức cao (vẫn có thể cho qua để reasoning xử lý 4B,
    # nhưng gắn cờ rủi ro; chỉ chặn khi có dấu hiệu tấn công rõ ràng)
    hard_block_flags = {
        "prompt_injection_ignore_instructions",
        "prompt_injection_ignore_instructions_vi",
        "prompt_leak_attempt",
        "prompt_leak_attempt_vi",
        "role_override_attempt",
        "secret_exfiltration_attempt",
        "sql_injection_pattern",
    }
    if any(f in hard_block_flags for f in flags):
        return SecurityDecision(
            False,
            "Yêu cầu bị chặn bởi AgentShield do dấu hiệu tấn công/bypass chính sách.",
            flags,
        )

    return SecurityDecision(True, "OK", flags or None)


def detect_sensitive_action(message: str) -> bool:
    lowered = (message or "").lower()
    return any(hint in lowered for hint in _SENSITIVE_ACTION_HINTS)


def detect_export_request(message: str) -> str | None:
    lowered = (message or "").lower()
    if "excel" in lowered or ".xlsx" in lowered or "xlsx" in lowered:
        return "xlsx"
    if "word" in lowered or ".docx" in lowered or "docx" in lowered:
        return "docx"
    if "pdf" in lowered:
        return "pdf"
    return None


def verify_internal_request(
    request: Request,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token"),
    x_libraryhub_internal: str | None = Header(default=None, alias="X-LibraryHub-Internal"),
) -> None:
    """Xác thực cuộc gọi nội bộ từ ASP.NET Core.

    - ASP.NET nên gửi `X-Internal-Token` khớp `CHATBOT_INTERNAL_API_KEY` sau khi đã
      xác thực phiên người dùng và CSRF tại tầng web.
    - Trong môi trường development, nếu chưa cấu hình key nội bộ thì cho phép để dễ tích hợp.
    """
    settings = get_settings()
    expected = (settings.chatbot_internal_api_key or "").strip()
    if not expected:
        if settings.app_env.lower() == "production":
            raise HTTPException(status_code=503, detail="Chưa cấu hình CHATBOT_INTERNAL_API_KEY cho production.")
        return
    provided = (x_internal_token or x_libraryhub_internal or "").strip()
    if not provided or provided != expected:
        raise HTTPException(status_code=401, detail="Yêu cầu nội bộ không hợp lệ (CSRF/Internal token).")

    # Kiểm tra nguồn gọi cơ bản (best-effort)
    host = (request.client.host if request.client else "") or ""
    # Không chặn cứng theo IP để linh hoạt khi deploy, nhưng có thể ghi nhận ở middleware/audit.
    _ = host


def is_read_only_role_allowed(role: str) -> bool:
    return role in {"Patron", "Librarian", "Admin"}
