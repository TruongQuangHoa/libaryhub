from __future__ import annotations

import json
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from .config import get_settings
from .schemas import ChatRequest


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def _checkpoint_file(checkpoint_id: str) -> Path:
    settings = get_settings()
    safe = "".join(ch for ch in checkpoint_id if ch.isalnum() or ch in "-_")[:80] or "checkpoint"
    return settings.checkpoint_path / f"{safe}.json"


def create_checkpoint(req: ChatRequest, reason: str) -> dict[str, Any]:
    settings = get_settings()
    settings.ensure_directories()
    checkpoint_id = f"cp-{uuid.uuid4().hex[:12]}"
    approval_token = uuid.uuid4().hex
    snapshot = {
        "checkpoint_id": checkpoint_id,
        "approval_token": approval_token,
        "created_at_utc": _now_iso(),
        "status": "pending_approval",
        "reason": reason,
        "requested_action": {
            "message": req.message,
            "role": req.role.value if hasattr(req.role, "value") else str(req.role),
            "user_id": req.user_id,
            "user_name": req.user_name,
            "page_context": req.page_context,
            "metadata": req.metadata or {},
        },
        "snapshot": {
            "note": "Snapshot logic cho tác vụ nhạy cảm: ghi nhận trạng thái yêu cầu trước khi thực thi.",
            "session_id": req.session_id or req.conversation_id,
        },
    }
    path = _checkpoint_file(checkpoint_id)
    path.write_text(json.dumps(snapshot, ensure_ascii=False, indent=2), encoding="utf-8")
    return snapshot


def load_checkpoint(checkpoint_id: str) -> dict[str, Any] | None:
    path = _checkpoint_file(checkpoint_id)
    if not path.exists():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return None


def approve_and_execute_checkpoint(checkpoint_id: str, approval_token: str) -> tuple[bool, str, dict[str, Any] | None]:
    cp = load_checkpoint(checkpoint_id)
    if not cp:
        return False, "Không tìm thấy checkpoint tương ứng (có thể đã hết hạn hoặc sai mã).", None
    if cp.get("status") == "approved_recorded":
        return True, "Checkpoint này đã được xác nhận/ghi nhận trước đó.", cp
    if cp.get("status") != "pending_approval":
        return False, f"Checkpoint không ở trạng thái chờ duyệt (status={cp.get('status')}).", cp
    expected = str(cp.get("approval_token") or "")
    if not approval_token or approval_token != expected:
        return False, "Mã xác nhận human-in-the-loop không hợp lệ.", cp

    # Thực thi an toàn: không trực tiếp thay đổi CSDL/cấu hình hệ thống từ chatbot.
    # Ghi nhận phê duyệt và để tác vụ được thực hiện qua quy trình/quyền quản trị phù hợp.
    cp["status"] = "approved_recorded"
    cp["approved_at_utc"] = _now_iso()
    cp["execution"] = {
        "mode": "controlled_no_direct_mutation",
        "result": "Đã ghi nhận phê duyệt và checkpoint. Chưa tự động thay đổi CSDL/cấu hình; cần thực hiện qua chức năng quản trị được cấp quyền.",
    }
    try:
        path = _checkpoint_file(checkpoint_id)
        path.write_text(json.dumps(cp, ensure_ascii=False, indent=2), encoding="utf-8")
    except Exception:
        return False, "Không thể lưu trạng thái phê duyệt checkpoint. Vui lòng thử lại.", cp
    return True, "Đã xác nhận checkpoint và ghi nhận tác vụ theo quy trình kiểm soát.", cp


def handle_sensitive_request(req: ChatRequest) -> tuple[str, bool, str | None, dict[str, Any]]:
    """4B. Cổng Phân quyền & Human-in-the-loop."""
    role = req.role.value if hasattr(req.role, "value") else str(req.role)
    meta: dict[str, Any] = {"branch": "4B"}

    # Nếu đã có checkpoint + approval token: chỉ Admin được xác nhận thực thi
    if req.checkpoint_id and req.approval_token:
        if role != "Admin":
            msg = (
                "Từ chối thực thi: tài khoản hiện không đủ quyền Quản trị để xác nhận tác vụ nhạy cảm. "
                "Vui lòng liên hệ Quản trị viên để được cấp quyền hoặc nhờ Quản trị viên xác nhận checkpoint."
            )
            meta.update({"decision": "reject_insufficient_role_for_approval"})
            return msg, False, req.checkpoint_id, meta
        ok, result_msg, cp = approve_and_execute_checkpoint(req.checkpoint_id, req.approval_token)
        meta.update({"decision": "approval_attempt", "approved": ok, "checkpoint_status": (cp or {}).get("status")})
        if ok:
            msg = (
                f"{result_msg}\n\n"
                f"- Checkpoint: `{req.checkpoint_id}`\n"
                "- Snapshot đã được lưu trước khi ghi nhận thực thi.\n"
                "- Lưu ý an toàn: chatbot không tự ý thay đổi trực tiếp cấu hình/CSDL; hãy thực hiện thao tác trong màn hình quản trị tương ứng sau khi đã được phê duyệt."
            )
            return msg, False, req.checkpoint_id, meta
        msg = (
            f"Không thể xác nhận thực thi tác vụ nhạy cảm: {result_msg}\n"
            "Vui lòng kiểm tra lại checkpoint/approval token hoặc tạo yêu cầu mới."
        )
        return msg, False, req.checkpoint_id, meta

    # Chưa có xác nhận: tạo checkpoint nếu đủ quyền Admin, ngược lại từ chối
    if role != "Admin":
        msg = (
            "Yêu cầu này thuộc nhóm thay đổi cấu hình/tác vụ nhạy cảm và **không được thực thi tự động**.\n\n"
            "- Kết quả cổng phân quyền (4B): **Không đủ quyền Quản trị**.\n"
            "- Hành động: Từ chối thực thi và hướng dẫn cấp quyền.\n\n"
            "Bạn vui lòng:\n"
            "1. Mô tả rõ thay đổi cần thực hiện và lý do nghiệp vụ.\n"
            "2. Liên hệ Quản trị viên LibraryHub để được cấp quyền hoặc thực hiện thay đổi.\n"
            "3. Nếu cần, cung cấp mốc thời gian/phạm vi ảnh hưởng để Quản trị viên đánh giá rủi ro."
        )
        meta.update({"decision": "reject_insufficient_permission"})
        return msg, False, None, meta

    try:
        cp = create_checkpoint(req, reason="Yêu cầu thay đổi cấu hình/tác vụ nhạy cảm cần human-in-the-loop.")
    except Exception as ex:
        msg = (
            "Yêu cầu thuộc nhóm tác vụ nhạy cảm và cần human-in-the-loop (4B), "
            f"nhưng hệ thống không thể tạo checkpoint/snapshot lúc này ({type(ex).__name__}). "
            "Vui lòng thử lại sau; tuyệt đối chưa thực thi thay đổi."
        )
        meta.update({"decision": "checkpoint_create_failed", "error": type(ex).__name__})
        return msg, False, None, meta

    checkpoint_id = cp["checkpoint_id"]
    approval_token = cp["approval_token"]
    msg = (
        "Yêu cầu này thuộc nhóm **thay đổi cấu hình/tác vụ nhạy cảm**. Hệ thống đã chuyển qua cổng phân quyền & human-in-the-loop (4B).\n\n"
        "- Kết quả phân quyền: **Đủ quyền Quản trị (xác thực vai trò)**.\n"
        f"- Đã tạo **Checkpoint & Snapshot**: `{checkpoint_id}`.\n"
        "- Chưa thực thi thay đổi để đảm bảo an toàn.\n\n"
        "Để tiếp tục (xác nhận human-in-the-loop), hãy gửi lại đúng lệnh sau (copy/paste):\n"
        f"`DUYET|{checkpoint_id}|{approval_token}`\n\n"
        "Hoặc gửi kèm trường `checkpoint_id` và `approval_token` qua API tích hợp.\n\n"
        "Khuyến nghị: rà soát phạm vi ảnh hưởng, sao lưu/cấu hình liên quan trước khi xác nhận. "
        "Sau xác nhận, chatbot sẽ ghi nhận phê duyệt theo checkpoint; thao tác thực tế nên thực hiện trong module quản trị tương ứng (chatbot không tự ý mutate CSDL/cấu hình)."
    )
    meta.update({"decision": "checkpoint_created", "checkpoint_id": checkpoint_id})
    return msg, True, checkpoint_id, meta
