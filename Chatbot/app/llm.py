from __future__ import annotations

import logging
import time
from dataclasses import dataclass
from typing import Any

import httpx

from .config import get_settings

logger = logging.getLogger(__name__)


@dataclass
class LLMResult:
    text: str
    success: bool
    model: str | None = None
    latency_ms: int | None = None
    error: str | None = None


ROLE_SYSTEM_PROMPTS = {
    "Admin": (
        "Bạn là trợ lý AI của hệ thống quản lý thư viện LibraryHub cho Quản trị viên. "
        "Hỗ trợ tra cứu vận hành, thống kê, người dùng, sách, mượn/trả và cảnh báo nghiệp vụ. "
        "Chỉ dựa trên dữ liệu/ngữ cảnh được cung cấp; nếu thiếu dữ liệu hãy nói rõ. "
        "Không hướng dẫn vượt quyền, không tiết lộ bí mật hệ thống. Trả lời tiếng Việt, rõ ràng, có cấu trúc."
    ),
    "Librarian": (
        "Bạn là trợ lý AI của LibraryHub cho Thủ thư. "
        "Hỗ trợ nghiệp vụ mượn/trả, độc giả, kho sách, tình trạng quá hạn và tra cứu thông tin vận hành được phép. "
        "Chỉ dựa trên dữ liệu/ngữ cảnh được cung cấp; nếu thiếu dữ liệu hãy nói rõ. "
        "Không thực thi thay đổi nhạy cảm khi chưa qua xác nhận/quy trình. Trả lời tiếng Việt, chính xác."
    ),
    "Patron": (
        "Bạn là trợ lý AI của LibraryHub cho Độc giả. "
        "Hỗ trợ tìm sách, tra cứu thông tin mượn/trả cá nhân, hướng dẫn sử dụng thư viện. "
        "Không cung cấp dữ liệu nội bộ của người khác hoặc thông tin quản trị. "
        "Chỉ dựa trên dữ liệu/ngữ cảnh được cung cấp. Trả lời tiếng Việt, thân thiện, dễ hiểu."
    ),
}


def _interface_behavior_note(role: str, interface_meta: dict[str, Any] | None, interface_context: str) -> str:
    meta = interface_meta or {}
    page_key = str(meta.get("pageKey") or "").strip()
    page_name = str(meta.get("pageName") or "").strip()
    is_multipurpose = bool(meta.get("isMultipurpose")) and role == "Admin"
    lines = [
        "[NGỮ CẢNH GIAO DIỆN HIỆN TẠI]",
        interface_context or "(Không có)",
        "",
        "Yêu cầu hành vi theo giao diện:",
        "- Trả lời phù hợp với mục tiêu và trọng tâm của trang người dùng đang sử dụng.",
        "- Nếu câu hỏi nằm ngoài trọng tâm trang nhưng vẫn trong quyền và có dữ liệu/ngữ cảnh, vẫn hỗ trợ; đồng thời gợi ý ngắn gọn cách thao tác đúng trang khi hữu ích.",
    ]
    if is_multipurpose:
        lines.extend([
            "- Đây là TRANG CHỦ QUẢN TRỊ: chatbot phải hoạt động ở chế độ ĐA NĂNG.",
            "- Có thể hỗ trợ xuyên suốt: tổng quan & thống kê, sách/thể loại/tác giả/NXB, bạn đọc/thủ thư/người dùng/phân quyền, mượn-trả/quá hạn/phạt/doanh thu, bài viết/đánh giá, hướng dẫn nghiệp vụ và xuất báo cáo.",
            "- Ưu tiên câu trả lời có tính tổng hợp, đề xuất bước tiếp theo phù hợp vai trò quản trị; với yêu cầu số liệu cần nêu rõ mốc thời gian/nguồn dữ liệu khi có.",
        ])
    elif page_key:
        lines.append(f"- Trang hiện tại: {page_name or page_key} (mã: {page_key}). Tập trung hỗ trợ đúng đặc thù của trang này.")
    return "\n".join(lines)


def _build_prompt(
    role: str,
    context_text: str,
    history_text: str,
    user_message: str,
    reasoning_note: str,
    interface_context: str = "",
    interface_meta: dict[str, Any] | None = None,
) -> tuple[str, list[dict[str, Any]]]:
    system = ROLE_SYSTEM_PROMPTS.get(role, ROLE_SYSTEM_PROMPTS["Patron"])
    interface_note = _interface_behavior_note(role, interface_meta, interface_context)
    system_text = (
        f"{system}\n\n"
        "Nguyên tắc trả lời:\n"
        "- Trung thực theo ngữ cảnh thực tế được nạp từ hệ thống/CSDL.\n"
        "- Nếu câu hỏi thiếu mốc thời gian, thiếu đối tượng hoặc có rủi ro hiểu sai, hãy yêu cầu làm rõ trước khi kết luận.\n"
        "- Với yêu cầu thay đổi cấu hình/tác vụ nhạy cảm: không tự ý thực thi, chỉ mô tả trạng thái phân quyền/điểm kiểm soát cần thiết.\n"
        "- Nếu có yêu cầu xuất file (Excel/Word/PDF), tóm tắt nội dung báo cáo sẽ được xuất.\n"
        "- Định dạng câu trả lời bằng Markdown gọn, dễ đọc.\n\n"
        f"{interface_note}"
    )

    user_text = (
        f"[PHÂN QUYỀN & VAI TRÒ]\n{role}\n\n"
        f"[NGỮ CẢNH ĐA TẦNG - TRI THỨC THỰC TẾ]\n{context_text or '(Không có dữ liệu ngữ cảnh bổ sung)'}\n\n"
        f"[LỊCH SỬ HỘI THOẠI & SESSION]\n{history_text or '(Không có lịch sử)'}\n\n"
        f"[KẾT QUẢ PHÂN LOẠI/SUY LUẬN]\n{reasoning_note or '(Không có)'}\n\n"
        f"[YÊU CẦU NGƯỜI DÙNG]\n{user_message}"
    )

    contents = [{"role": "user", "parts": [{"text": user_text}]}]
    return system_text, contents


async def generate_answer(
    *,
    role: str,
    user_message: str,
    context_text: str,
    history_text: str,
    reasoning_note: str,
    interface_context: str = "",
    interface_meta: dict[str, Any] | None = None,
) -> LLMResult:
    settings = get_settings()
    creds = settings.gemini_credentials()
    if not creds:
        return LLMResult(
            text=(
                "Chatbot chưa được cấu hình Gemini API Key/Model. "
                "Vui lòng kiểm tra biến môi trường trong thư mục Chatbot/.env."
            ),
            success=False,
            error="missing_gemini_credentials",
        )

    system_text, contents = _build_prompt(
        role,
        context_text,
        history_text,
        user_message,
        reasoning_note,
        interface_context=interface_context,
        interface_meta=interface_meta,
    )
    payload = {
        "systemInstruction": {"parts": [{"text": system_text}]},
        "contents": contents,
        "generationConfig": {
            "temperature": 0.2,
            "topP": 0.9,
            "maxOutputTokens": 2048,
        },
    }

    last_error: str | None = None
    timeout = httpx.Timeout(settings.request_timeout_seconds)
    async with httpx.AsyncClient(timeout=timeout) as client:
        for cred in creds:
            api_key = cred["api_key"]
            model = cred.get("model") or settings.gemini_model
            url = f"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent"
            for attempt in range(1, 4):
                started = time.perf_counter()
                try:
                    resp = await client.post(
                        url,
                        params={"key": api_key},
                        json=payload,
                    )
                    latency_ms = int((time.perf_counter() - started) * 1000)
                    if resp.status_code == 200:
                        data = resp.json()
                        text = _extract_text(data)
                        return LLMResult(text=text, success=True, model=model, latency_ms=latency_ms)
                    if resp.status_code in (429, 503) and attempt < 3:
                        # thử lại với cùng credential
                        await _async_sleep(attempt * 1.5)
                        last_error = f"HTTP {resp.status_code}"
                        continue
                    # lỗi khác hoặc hết lần thử: chuyển credential kế tiếp nếu có
                    last_error = f"HTTP {resp.status_code}: {resp.text[:300]}"
                    break
                except Exception as exc:
                    latency_ms = int((time.perf_counter() - started) * 1000)
                    last_error = f"{type(exc).__name__}: {exc}"
                    logger.warning("Gemini request failed (model=%s, attempt=%s): %s", model, attempt, exc)
                    if attempt < 3:
                        await _async_sleep(attempt * 1.0)
                        continue
                    break
        return LLMResult(
            text=(
                "Xin lỗi, tôi chưa thể kết nối tới mô hình ngôn ngữ lúc này. "
                "Vui lòng thử lại sau hoặc kiểm tra cấu hình Gemini."
            ),
            success=False,
            model=creds[-1].get("model") if creds else None,
            error=last_error,
        )


def _extract_text(data: dict[str, Any]) -> str:
    try:
        candidates = data.get("candidates") or []
        if not candidates:
            return "Mô hình chưa tạo được câu trả lời. Vui lòng thử lại."
        parts = (((candidates[0] or {}).get("content") or {}).get("parts")) or []
        texts = [p.get("text", "") for p in parts if isinstance(p, dict) and p.get("text")]
        answer = "\n".join(t.strip() for t in texts if t and t.strip()).strip()
        return answer or "Mô hình chưa trả về nội dung câu trả lời."
    except Exception:
        return "Không thể đọc nội dung phản hồi từ mô hình."


async def _async_sleep(seconds: float) -> None:
    import asyncio

    await asyncio.sleep(seconds)
