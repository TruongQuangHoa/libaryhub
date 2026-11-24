from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from .read_tools import collect_read_context
from .schemas import ChatRequest
from .session_store import recent_history_text


@dataclass
class ContextBundle:
    role_context: str
    history_context: str
    knowledge_context: str
    interface_context: str
    sources: list[str]
    combined_text: str
    interface_meta: dict[str, Any] = field(default_factory=dict)


ROLE_PERMISSIONS = {
    "Admin": (
        "- Toàn quyền tra cứu dữ liệu vận hành thư viện (sách, độc giả, mượn/trả, thống kê, doanh thu, cảnh báo) trong phạm vi được cấp.\n"
        "- Có thể yêu cầu tác vụ nhạy cảm/thay đổi cấu hình, nhưng phải qua cổng phân quyền, checkpoint/snapshot và xác nhận human-in-the-loop trước khi thực thi."
    ),
    "Librarian": (
        "- Được tra cứu dữ liệu nghiệp vụ thư viện (sách, độc giả, mượn/trả, quá hạn, thống kê phục vụ vận hành) theo quyền được cấp.\n"
        "- Không được tự ý thực thi thay đổi cấu hình/tác vụ nhạy cảm; cần quyền Quản trị và quy trình xác nhận phù hợp."
    ),
    "Patron": (
        "- Chỉ được tra cứu thông tin công khai (sách, thể loại, bài viết) và dữ liệu cá nhân của chính mình (phiếu mượn/lịch sử liên quan tài khoản).\n"
        "- Không được truy cập dữ liệu quản trị, dữ liệu người khác, hoặc yêu cầu thay đổi cấu hình hệ thống."
    ),
    "Guest": "- Không có quyền truy cập chatbot.",
}


def _extract_interface_meta(req: ChatRequest) -> dict[str, Any]:
    meta = req.metadata if isinstance(req.metadata, dict) else {}
    raw = meta.get("interface") if isinstance(meta, dict) else None
    interface: dict[str, Any] = dict(raw) if isinstance(raw, dict) else {}
    # Bổ sung suy luận từ page_context nếu thiếu
    pc = (req.page_context or "").strip()
    if pc and "pageContext" not in interface and "page_context" not in interface:
        interface.setdefault("pageContext", pc)
    key = str(interface.get("pageKey") or interface.get("page_key") or "").strip()
    name = str(interface.get("pageName") or interface.get("page_name") or "").strip()
    area = str(interface.get("area") or "").strip()
    controller = str(interface.get("controller") or "").strip()
    action = str(interface.get("action") or "").strip()
    is_multipurpose = bool(interface.get("isMultipurpose") or interface.get("is_multipurpose"))
    if not key:
        lower_pc = pc.lower()
        if "/admin" in lower_pc and ("home" in lower_pc or lower_pc.rstrip("/").endswith("/admin")):
            key = "admin-home"
            is_multipurpose = True
            name = name or "Trang chủ quản trị"
    interface["pageKey"] = key
    interface["pageName"] = name
    interface["area"] = area
    interface["controller"] = controller
    interface["action"] = action
    interface["isMultipurpose"] = is_multipurpose
    return interface


def _build_interface_context(role: str, page_context: str | None, interface: dict[str, Any]) -> str:
    page_key = str(interface.get("pageKey") or "").strip()
    page_name = str(interface.get("pageName") or "").strip() or (page_context or "Không xác định")
    area = str(interface.get("area") or "").strip()
    controller = str(interface.get("controller") or "").strip()
    action = str(interface.get("action") or "").strip()
    is_multipurpose = bool(interface.get("isMultipurpose"))

    lines = [
        f"- Trang/Giao diện: {page_context or 'N/A'}",
        f"- Khu vực (area): {area or 'Site'}",
        f"- Controller/Action: {controller or 'N/A'}/{action or 'N/A'}",
        f"- Mã trang: {page_key or 'N/A'}",
        f"- Tên trang: {page_name}",
    ]

    if is_multipurpose and role == "Admin":
        lines.append("- Chế độ: **ĐA NĂNG (trang chủ quản trị)** — hỗ trợ tổng hợp xuyên suốt các nghiệp vụ quản trị.")
        lines.append(
            "- Phạm vi ưu tiên: tổng quan hệ thống, sách/thể loại/tác giả/NXB, độc giả/thủ thư/người dùng/phân quyền, "
            "mượn-trả/quá hạn/phạt, thống kê & doanh thu, bài viết/đánh giá, hướng dẫn vận hành và xuất báo cáo."
        )
    elif area.lower() == "admin":
        lines.append("- Chế độ: theo ngữ cảnh trang quản trị hiện tại; ưu tiên nghiệp vụ của trang này nhưng vẫn hỗ trợ tra cứu liên quan trong quyền.")
    else:
        lines.append("- Chế độ: theo ngữ cảnh trang người dùng hiện tại; ưu tiên hỗ trợ đúng mục tiêu của trang.")

    # Gợi ý đặc thù theo trang
    hints = {
        "admin-home": "Tổng hợp đa lĩnh vực, đối chiếu số liệu nhanh, điều hướng nghiệp vụ quản trị, báo cáo/xuất file.",
        "admin-books": "Quản lý/tra cứu đầu sách-bản sách, tồn kho, thể loại, tác giả/NXB, tình trạng mượn.",
        "admin-borrowing": "Phiếu mượn, chi tiết mượn, đang mượn/quá hạn, lượt mượn theo thời gian.",
        "admin-return": "Phiếu trả, tiền phạt, doanh thu phạt, xử lý nghiệp vụ trả.",
        "admin-patrons": "Thông tin bạn đọc, tài khoản, lịch sử mượn/trả, vi phạm/quá hạn.",
        "admin-librarians": "Thông tin thủ thư và hoạt động liên quan.",
        "admin-users": "Người dùng, vai trò, phân quyền (thay đổi nhạy cảm cần HITL).",
        "admin-statistics": "Thống kê, doanh thu, lượt mượn/phạt, yêu cầu mốc thời gian rõ và xuất báo cáo.",
        "admin-posts": "Bài viết/tin tức/nội dung thông báo.",
        "admin-categories": "Thể loại và cơ cấu danh mục sách.",
        "admin-authors": "Tác giả và liên kết đầu sách.",
        "admin-publishers": "Nhà xuất bản và đầu sách liên quan.",
        "admin-reviews": "Đánh giá/phản hồi bạn đọc.",
        "site-home": "Định hướng nhanh, gợi ý sách/nội dung, hướng dẫn sử dụng thư viện.",
        "site-books": "Tra cứu sách theo tên/tác giả/thể loại/tình trạng, gợi ý lựa chọn.",
        "site-borrowing-cart": "Quy trình đăng ký mượn, điều kiện, kiểm tra trạng thái mượn.",
        "site-profile": "Hồ sơ cá nhân, phiếu đang mượn, lịch sử, quá hạn (phạm vi tài khoản).",
        "site-posts": "Tin tức/bài viết/thông báo thư viện.",
        "site-contact": "Thông tin liên hệ, kênh hỗ trợ, giờ làm việc.",
        "site-introduction": "Giới thiệu thư viện, dịch vụ, quy định cơ bản.",
        "site-reviews": "Đánh giá/phản hồi về thư viện.",
    }
    hint = hints.get(page_key)
    if hint:
        lines.append(f"- Trọng tâm trang: {hint}")

    return "\n".join(lines)


def build_context(req: ChatRequest) -> ContextBundle:
    role = req.role.value if hasattr(req.role, "value") else str(req.role)
    interface_meta = _extract_interface_meta(req)
    interface_context = _build_interface_context(role, req.page_context, interface_meta)

    role_context = (
        f"Vai trò: {role}\n"
        f"UserID: {req.user_id}\n"
        f"Tên người dùng: {req.user_name or 'N/A'}\n"
        f"Quyền hạn:\n{ROLE_PERMISSIONS.get(role, ROLE_PERMISSIONS['Guest'])}"
    )

    history_context = recent_history_text(req.session_id or req.conversation_id)

    knowledge_context, sources = collect_read_context(req.message, role, req.user_id)
    if not knowledge_context:
        knowledge_context = "(Không truy xuất được dữ liệu CSDL phù hợp cho yêu cầu này.)"

    combined = (
        f"### 1) Phân quyền Tài khoản & Vai trò\n{role_context}\n\n"
        f"### 2) Ngữ cảnh Giao diện/Trang (đặc thù)\n{interface_context}\n\n"
        f"### 3) Lịch sử Cuộc trò chuyện & Session\n{history_context}\n\n"
        f"### 4) Dữ liệu CSDL/Thực tế (Read-Tools)\n{knowledge_context}"
    )
    return ContextBundle(
        role_context=role_context,
        history_context=history_context,
        knowledge_context=knowledge_context,
        interface_context=interface_context,
        sources=sources,
        combined_text=combined,
        interface_meta=interface_meta,
    )
