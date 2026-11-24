from __future__ import annotations

import logging
import time
import traceback
from typing import Any

from fastapi import Depends, FastAPI, HTTPException, Request
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from fastapi.staticfiles import StaticFiles

from . import audit
from .config import get_settings
from .context_engine import build_context
from .export_service import create_export
from .llm import generate_answer
from .reasoning import classify_request
from .response_formatter import build_response, format_markdown_answer
from .schemas import ChatRequest, IntentKind
from .security import agentshield_scan, detect_sensitive_action, verify_internal_request
from .session_store import save_turn
from .write_gate import handle_sensitive_request


def _normalize_hitl_request(req: ChatRequest) -> ChatRequest:
    """Cho phép người dùng xác nhận 4B trực tiếp qua tin nhắn chat: DUYET|checkpoint_id|approval_token."""
    if req.checkpoint_id and req.approval_token:
        return req
    msg = (req.message or "").strip()
    if not msg.upper().startswith(("DUYET", "DUYỆT", "APPROVE")):
        return req
    for sep in ("|", ":", " "):
        parts = [p.strip() for p in msg.split(sep) if p.strip()]
        # Hỗ trợ: DUYET|cp-xxx|token hoặc DUYET cp-xxx token
        if len(parts) >= 3 and parts[0].upper() in {"DUYET", "DUYỆT", "APPROVE"}:
            try:
                return req.model_copy(update={
                    "checkpoint_id": parts[1],
                    "approval_token": parts[2],
                    "message": "Xác nhận human-in-the-loop cho checkpoint đã tạo.",
                })
            except Exception:
                return req
    return req

logger = logging.getLogger(__name__)
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")

settings = get_settings()
app = FastAPI(title=settings.app_name, version="1.0.0")

# CORS: cho phép tích hợp linh hoạt; production nên giới hạn origin tại reverse proxy/ASP.NET.
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=False,
    allow_methods=["GET", "POST", "OPTIONS"],
    allow_headers=["*"],
)

# Phục vụ tệp xuất (Excel/Word/PDF) để giao diện hiển thị nút tải
try:
    settings.ensure_directories()
    app.mount(settings.export_base_url, StaticFiles(directory=str(settings.export_path)), name="exports")
except Exception:
    logger.exception("Không thể mount thư mục exports; tính năng tải file có thể không khả dụng.")


@app.get("/health")
async def health() -> dict[str, Any]:
    return {"status": "ok", "service": settings.app_name, "env": settings.app_env}


@app.exception_handler(HTTPException)
async def http_exception_handler(request: Request, exc: HTTPException):
    # Chuẩn hóa lỗi theo contract {success,message} để dễ tích hợp với ASP.NET/UI hiện tại
    return JSONResponse(
        status_code=exc.status_code,
        content={"success": False, "message": str(exc.detail)},
    )


@app.exception_handler(RequestValidationError)
async def validation_exception_handler(request: Request, exc: RequestValidationError):
    # Không lộ chi tiết schema nội bộ; trả thông báo thân thiện và nhất quán
    logger.info("Validation error on %s: %s", request.url.path, exc.errors())
    msg = "Dữ liệu yêu cầu không hợp lệ. Vui lòng kiểm tra nội dung tin nhắn và thử lại."
    try:
        errs = exc.errors() or []
        if errs:
            loc = ".".join(str(x) for x in (errs[0].get("loc") or []) if x != "body")
            if "message" in loc:
                msg = "Vui lòng nhập câu hỏi hợp lệ (không để trống và không vượt quá giới hạn cho phép)."
    except Exception:
        pass
    return JSONResponse(status_code=422, content={"success": False, "message": msg})


@app.exception_handler(Exception)
async def unhandled_exception_handler(request: Request, exc: Exception):
    logger.exception("Unhandled exception at %s: %s", request.url.path, exc)
    return JSONResponse(
        status_code=500,
        content={"success": False, "message": "Đã xảy ra lỗi nội bộ khi xử lý yêu cầu Chatbot. Vui lòng thử lại sau."},
    )


async def _process_chat(req: ChatRequest, request: Request) -> dict[str, Any]:
    started = time.perf_counter()
    req = _normalize_hitl_request(req)
    role = req.role.value if hasattr(req.role, "value") else str(req.role)
    session_id = req.session_id or req.conversation_id or f"anon-{role.lower()}"

    # 1. Cổng kiểm soát an ninh AgentShield & CSRF/Internal trust
    try:
        decision = agentshield_scan(req.message)
    except Exception as ex:
        logger.exception("AgentShield scan error: %s", ex)
        decision = type("Decision", (), {"allowed": False, "reason": "Lỗi kiểm tra an ninh đầu vào.", "risk_flags": ["scan_error"]})()

    if not decision.allowed:
        audit_id = audit.log_audit_event(
            event_type="request_blocked",
            role=role,
            user_id=req.user_id,
            user_name=req.user_name,
            session_id=session_id,
            allowed=False,
            reason=getattr(decision, "reason", "blocked"),
            input_text=req.message,
            metadata={"risk_flags": getattr(decision, "risk_flags", None) or [], "path": str(request.url.path)},
        )
        resp = build_response(
            success=False,
            message=f"Yêu cầu bị từ chối bởi cổng kiểm soát an ninh: {getattr(decision, 'reason', 'blocked')}",
            intent="blocked",
            risk_level="high",
            audit_id=audit_id,
        )
        return resp.model_dump()

    audit_id = audit.new_audit_id()
    audit.log_audit_event(
        audit_id=audit_id,
        event_type="request_received",
        role=role,
        user_id=req.user_id,
        user_name=req.user_name,
        session_id=session_id,
        allowed=True,
        reason="passed_agentshield",
        input_text=req.message,
        metadata={"risk_flags": getattr(decision, "risk_flags", None) or [], "page_context": req.page_context},
    )

    context = None
    reasoning = None
    try:
        # 2. Động cơ ngữ cảnh đa tầng (DB, lịch sử/session, phân quyền & role)
        context = build_context(req)

        # 3. Động cơ suy luận tự chủ (phân loại rủi ro/ý định/luồng xử lý)
        reasoning = classify_request(req)
    except Exception as ex:
        logger.exception("Context/reasoning error: %s", ex)
        audit.log_audit_event(
            audit_id=audit_id,
            event_type="request_failed",
            role=role,
            user_id=req.user_id,
            user_name=req.user_name,
            session_id=session_id,
            allowed=True,
            reason="context_reasoning_error",
            input_text=req.message,
            output_text=str(ex),
            metadata={"traceback": traceback.format_exc()[-2000:]},
        )
        return {
            "success": False,
            "message": "Không thể xây dựng ngữ cảnh xử lý yêu cầu lúc này. Vui lòng thử lại sau.",
            "audit_id": audit_id,
        }

    export_type = reasoning.requested_export
    requires_human_approval = False
    checkpoint_id = None
    base_message = ""
    final_message = ""
    llm_used = False

    try:
        # Nhánh 4B: thay đổi cấu hình / tác vụ nhạy cảm / xác nhận HITL -> cổng phân quyền & human-in-the-loop
        is_hitl_approval = bool(req.checkpoint_id and req.approval_token)
        is_sensitive = bool(
            reasoning.intent == IntentKind.sensitive_action
            or detect_sensitive_action(req.message)
            or is_hitl_approval
        )
        if is_sensitive:
            try:
                gated_message, requires_human_approval, checkpoint_id, gate_meta = handle_sensitive_request(req)
            except Exception as ex:
                logger.exception("Sensitive gate error: %s", ex)
                gated_message = "Không thể xử lý yêu cầu nhạy cảm tại cổng kiểm soát 4B lúc này. Vui lòng thử lại hoặc liên hệ Quản trị viên."
                requires_human_approval = False
                checkpoint_id = req.checkpoint_id
                gate_meta = {"decision": "gate_error", "error": type(ex).__name__}
            base_message = gated_message
            audit.log_audit_event(
                audit_id=audit_id,
                event_type="sensitive_action_gate",
                role=role,
                user_id=req.user_id,
                user_name=req.user_name,
                session_id=session_id,
                allowed=True,
                reason=gate_meta.get("decision") if isinstance(gate_meta, dict) else None,
                intent=(IntentKind.sensitive_action.value if (is_hitl_approval or reasoning.intent == IntentKind.sensitive_action) else reasoning.intent.value),
                risk_level=reasoning.risk_level.value,
                output_text=base_message,
                metadata=gate_meta if isinstance(gate_meta, dict) else {"hitl_approval": is_hitl_approval},
            )

        # Nhánh thiếu mốc thời gian/rủi ro cao: phân tích NỀN vs KHÔNG NỀN, yêu cầu làm rõ
        elif reasoning.requires_clarification and reasoning.clarification_message:
            clarification = reasoning.clarification_message
            base_message = (
                "## Phân tích theo yêu cầu (trước khi kết luận)\n"
                "- **Phân tích NỀN (dựa trên dữ liệu hệ thống):** " +
                ("Có thể đối chiếu với CSDL/phiên làm việc sau khi bạn cung cấp mốc thời gian và đối tượng cụ thể." if context.sources else "Chưa đủ dữ liệu ngữ cảnh để đối chiếu chính xác.") +
                "\n- **Phân tích KHÔNG NỀN (giả định ngoài dữ liệu):** Mọi suy đoán khi thiếu mốc thời gian/phạm vi đều có rủi ro sai lệch và sẽ không được dùng làm kết luận.\n\n"
                + clarification
            )
            audit.log_audit_event(
                audit_id=audit_id,
                event_type="clarification_required",
                role=role,
                user_id=req.user_id,
                user_name=req.user_name,
                session_id=session_id,
                allowed=True,
                reason="missing_time_or_high_risk_context",
                intent=reasoning.intent.value,
                risk_level=reasoning.risk_level.value,
                output_text=base_message,
                metadata={"analysis_mode": reasoning.analysis_mode},
            )

        # Nhánh 4A: tra cứu read-only + tổng hợp tri thức
        else:
            llm_used = True
            try:
                llm_result = await generate_answer(
                    role=role,
                    user_message=req.message,
                    context_text=context.combined_text,
                    history_text=context.history_context,
                    reasoning_note=reasoning.notes,
                    interface_context=getattr(context, "interface_context", ""),
                    interface_meta=getattr(context, "interface_meta", None),
                )
            except Exception as ex:
                logger.exception("LLM generation error: %s", ex)
                llm_result = type("LLMResult", (), {
                    "text": "Xin lỗi, tôi chưa thể tạo câu trả lời từ mô hình lúc này.",
                    "success": False,
                    "model": None,
                    "latency_ms": None,
                    "error": f"{type(ex).__name__}: {ex}",
                })()

            audit.log_ai_usage(
                audit_id=audit_id,
                model=getattr(llm_result, "model", None),
                prompt_chars=len(req.message or "") + len(getattr(context, "combined_text", "") or ""),
                response_chars=len(getattr(llm_result, "text", "") or ""),
                success=bool(getattr(llm_result, "success", False)),
                latency_ms=getattr(llm_result, "latency_ms", None),
                error=getattr(llm_result, "error", None),
            )
            base_message = (getattr(llm_result, "text", None) or "Không nhận được phản hồi từ mô hình.").strip()
            if not getattr(llm_result, "success", False):
                # Vẫn trả lời hữu ích dựa trên ngữ cảnh đọc được nếu có
                kc = (context.knowledge_context or "").strip()
                if kc and kc != "(Không truy xuất được dữ liệu CSDL phù hợp cho yêu cầu này.)":
                    base_message = (
                        base_message
                        + "\n\n---\n"
                        + "**Dữ liệu tra cứu được từ hệ thống (Read-Tools):**\n"
                        + kc[:3500]
                    )
                else:
                    base_message = (
                        base_message
                        + "\n\n> Hiện chưa truy xuất được dữ liệu hệ thống phù hợp cho yêu cầu này. "
                        + "Vui lòng thử mô tả cụ thể hơn (mã/tên sách, khoảng thời gian, đối tượng cần tra cứu) hoặc kiểm tra kết nối CSDL/dịch vụ Chatbot."
                    )

        # 5. Tổng hợp tri thức & định dạng phản hồi + xuất file nếu được yêu cầu
        export_info = None
        if export_type in {"xlsx", "docx", "pdf"}:
            try:
                title = "Báo cáo LibraryHub - Chatbot"
                export_content = base_message
                kc = (context.knowledge_context or "").strip()
                if kc and "[Nội dung tra cứu thực tế]" not in export_content:
                    export_content = (
                        export_content
                        + "\n\n---\n[Nội dung tra cứu thực tế]\n"
                        + kc[:12000]
                    )
                export_info = create_export(export_type, export_content, title=title)
            except Exception as ex:
                logger.exception("Lỗi tạo file xuất: %s", ex)
                base_message += f"\n\n> Không thể tạo file {export_type.upper()} lúc này: {type(ex).__name__}."

        final_message = format_markdown_answer(
            base_message,
            export=export_info,
            checkpoint_id=checkpoint_id,
            requires_human_approval=requires_human_approval,
        )

        # Ghi nhận lịch sử session và audit hoàn tất
        save_turn(session_id, req.message, final_message, meta={"audit_id": audit_id, "intent": reasoning.intent.value})
        audit.log_audit_event(
            audit_id=audit_id,
            event_type="request_completed",
            role=role,
            user_id=req.user_id,
            user_name=req.user_name,
            session_id=session_id,
            allowed=True,
            reason="completed",
            intent=reasoning.intent.value,
            risk_level=reasoning.risk_level.value,
            input_text=req.message,
            output_text=final_message,
            metadata={
                "sources": context.sources,
                "llm_used": llm_used,
                "export": export_info,
                "requires_human_approval": requires_human_approval,
                "checkpoint_id": checkpoint_id,
                "latency_ms": int((time.perf_counter() - started) * 1000),
            },
        )

        resp = build_response(
            success=True,
            message=final_message,
            intent=reasoning.intent.value,
            risk_level=reasoning.risk_level.value,
            requires_human_approval=requires_human_approval,
            checkpoint_id=checkpoint_id,
            audit_id=audit_id,
            export=export_info,
            sources=context.sources,
        )
        return resp.model_dump()

    except Exception as ex:
        logger.exception("Unhandled chatbot error: %s", ex)
        try:
            audit.log_audit_event(
                audit_id=audit_id,
                event_type="request_failed",
                role=role,
                user_id=req.user_id,
                user_name=req.user_name,
                session_id=session_id,
                allowed=True,
                reason=type(ex).__name__,
                intent=reasoning.intent.value if reasoning is not None else None,
                risk_level=reasoning.risk_level.value if reasoning is not None else None,
                input_text=req.message,
                output_text=str(ex),
                metadata={"traceback": traceback.format_exc()[-2000:]},
            )
        except Exception:
            pass
        return {
            "success": False,
            "message": "Đã xảy ra lỗi khi xử lý yêu cầu Chatbot. Vui lòng thử lại sau.",
            "audit_id": audit_id,
        }


@app.post("/chat/send")
async def chat_send(
    payload: ChatRequest,
    request: Request,
    _auth: None = Depends(verify_internal_request),
):
    # Ủy quyền vai trò: không tin role từ client nếu thiếu; nhưng khi tích hợp qua ASP.NET,
    # ASP.NET gửi role đã xác thực. Vẫn từ chối Guest.
    role = payload.role.value if hasattr(payload.role, "value") else str(payload.role)
    if role not in {"Patron", "Admin", "Librarian"}:
        raise HTTPException(status_code=403, detail="Không có quyền truy cập Chatbot.")
    result = await _process_chat(payload, request)
    status = 200 if result.get("success") else 400
    # Giữ 200 cho các trường hợp blocked/clarification có thông điệp nghiệp vụ? 
    # Nếu success False do block nghiệp vụ, trả 200 với payload success=false để UI hiển thị message nhất quán.
    if result.get("intent") == "blocked" or result.get("message"):
        status = 200
    return JSONResponse(status_code=status, content=result)


@app.post("/api/chat/send")
async def api_chat_send(
    payload: ChatRequest,
    request: Request,
    _auth: None = Depends(verify_internal_request),
):
    return await chat_send(payload, request, _auth)
