from __future__ import annotations

import re
from dataclasses import dataclass

from .schemas import ChatRequest, IntentKind, RiskLevel
from .security import detect_export_request, detect_sensitive_action


@dataclass
class ReasoningResult:
    intent: IntentKind
    risk_level: RiskLevel
    requires_clarification: bool
    clarification_message: str | None
    analysis_mode: str  # "read_lookup" | "background_analysis" | "foreground_analysis" | "sensitive_flow"
    requested_export: str | None
    notes: str


_TIME_MISSING_HINTS = (
    "hôm nay", "hôm qua", "tuần này", "tháng này", "năm nay", "gần đây", "vừa rồi",
    "recent", "lúc nào", "khi nào", "thời gian nào", "bao lâu", "trước đây",
)

_TIME_EXPLICIT_RE = re.compile(
    r"\b(\d{1,2}[/-]\d{1,2}([/-]\d{2,4})?|\d{4}|quý\s*\d|tháng\s*\d{1,2}|năm\s*20\d{2}|"
    r"\d{1,2}:\d{2}|\d{1,2}\s*giờ)\b",
    re.IGNORECASE,
)

_PROFILE_LOOKUP_HINTS = (
    "tra hồ sơ", "hồ sơ", "lịch sử", "log", "nhật ký", "ai đã", "thay đổi gì",
    "mốc thời gian", "timeline", "diễn biến", "truy vết",
)

_HIGH_RISK_HINTS = (
    "xóa dữ liệu", "xoá dữ liệu", "drop", "truncate", "reset hệ thống", "phân quyền",
    "cấp quyền", "đổi mật khẩu", "thanh toán", "giao dịch", "bí mật", "secret",
)


def classify_request(req: ChatRequest) -> ReasoningResult:
    msg = (req.message or "").strip()
    lowered = msg.lower()

    requested_export = req.requested_export or detect_export_request(msg)
    sensitive = detect_sensitive_action(msg)

    has_profile_lookup = any(h in lowered for h in _PROFILE_LOOKUP_HINTS)
    has_missing_time_phrase = any(h in lowered for h in _TIME_MISSING_HINTS)
    has_explicit_time = bool(_TIME_EXPLICIT_RE.search(msg))
    missing_time = (has_profile_lookup or has_missing_time_phrase) and not has_explicit_time

    high_risk = any(h in lowered for h in _HIGH_RISK_HINTS) or sensitive

    # 3. Động cơ Suy luận Tự chủ: phân nhánh theo sơ đồ
    if sensitive:
        intent = IntentKind.sensitive_action
        risk = RiskLevel.high if high_risk else RiskLevel.medium
        notes = (
            "Phát hiện yêu cầu thay đổi cấu hình/tác vụ nhạy cảm. "
            "Chuyển sang cổng phân quyền & human-in-the-loop (4B), yêu cầu checkpoint/snapshot trước khi thực thi."
        )
        return ReasoningResult(
            intent=intent,
            risk_level=risk,
            requires_clarification=False,
            clarification_message=None,
            analysis_mode="sensitive_flow",
            requested_export=requested_export,
            notes=notes,
        )

    if missing_time and (has_profile_lookup or high_risk):
        intent = IntentKind.missing_context
        risk = RiskLevel.high if high_risk else RiskLevel.medium
        clarification = (
            "Yêu cầu có dấu hiệu tra hồ sơ/thiếu mốc thời gian hoặc rủi ro hiểu sai. "
            "Vui lòng bổ sung: (1) khoảng thời gian cụ thể, (2) đối tượng cần tra cứu (sách/độc giả/phiếu mượn/mã), "
            "(3) phạm vi dữ liệu (cá nhân hay toàn hệ thống, nếu bạn có quyền). "
            "Tôi sẽ phân tích theo bối cảnh NỀN (dữ liệu hệ thống) và KHÔNG NỀN (giả định ngoài dữ liệu) sau khi có đủ thông tin."
        )
        notes = (
            "Thiếu mốc thời gian/rủi ro cao: cần hồi ngược làm rõ và phân biệt phân tích NỀN vs KHÔNG NỀN trước khi kết luận."
        )
        return ReasoningResult(
            intent=intent,
            risk_level=risk,
            requires_clarification=True,
            clarification_message=clarification,
            analysis_mode="background_analysis",
            requested_export=requested_export,
            notes=notes,
        )

    # Tra cứu thông tin / read-only
    intent = IntentKind.read_lookup
    risk = RiskLevel.low
    notes = "Yêu cầu tra cứu/thông tin: ưu tiên điều phối Read-Tools (4A) và trả lời dựa trên dữ liệu thực tế."
    return ReasoningResult(
        intent=intent,
        risk_level=risk,
        requires_clarification=False,
        clarification_message=None,
        analysis_mode="read_lookup",
        requested_export=requested_export,
        notes=notes,
    )
