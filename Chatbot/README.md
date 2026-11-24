# LibraryHub Chatbot (Python)

Thư mục này chứa chương trình Python xây dựng Chatbot tích hợp cho giao diện **Patron**, **Librarian**, và **Admin** của dự án ASP.NET Core MVC **LibraryHub**.

Thiết kế bám theo sơ đồ xử lý yêu cầu:
1. **Cổng kiểm soát an ninh AgentShield & CSRF**: lọc prompt-injection/mẫu tấn công cơ bản, kiểm tra token nội bộ khi ASP.NET gọi sang, chặn và ghi audit log khi vi phạm.
2. **Động cơ Ngữ cảnh Đa tầng**: nạp ngữ cảnh từ
   - Dữ liệu CSDL thực tế (read-only qua SQL Server)
   - Lịch sử hội thoại & session
   - Phân quyền tài khoản & vai trò (Patron/Librarian/Admin)
3. **Động cơ Suy luận Tự chủ**: phân loại ý định/rủi ro và chọn nhánh xử lý
   - Tra cứu read-only → điều phối Read-Tools (4A)
   - Thiếu mốc thời gian/rủi ro cao/tra hồ sơ → yêu cầu làm rõ và phân biệt phân tích **NỀN** (dữ liệu hệ thống) vs **KHÔNG NỀN** (giả định)
   - Thay đổi cấu hình/tác vụ nhạy cảm → cổng phân quyền & Human-in-the-loop (4B)
4. **4A Read-Tools**: tra cứu sách, thể loại, thống kê, doanh thu/lượt mượn, cảnh báo quá hạn, lịch sử mượn cá nhân (theo quyền), bài viết/tin tức.
5. **4B Cổng phân quyền & Human-in-the-loop**:
   - Không đủ quyền (đặc biệt tác vụ nhạy cảm không phải Admin): từ chối thực thi và hướng dẫn cấp quyền
   - Đủ quyền Admin: tạo **Checkpoint & Snapshot**, yêu cầu xác nhận `checkpoint_id` + `approval_token` trước khi ghi nhận thực thi kiểm soát (không tự ý mutate CSDL/cấu hình trực tiếp)
6. **Tổng hợp tri thức & định dạng phản hồi**: trả lời Markdown dựa trên ngữ cảnh thực tế; nếu yêu cầu xuất **Excel/Word/PDF** thì tạo tệp báo cáo và trả link tải.
7. **Ghi nhận Audit Log & AI Usage**: lưu JSONL theo ngày trong `data/audit/`.

## Cấu trúc thư mục

```text
Chatbot/
  app/
    main.py              # FastAPI app, endpoint /chat/send, /health
    config.py            # Nạp cấu hình từ .env
    schemas.py           # Pydantic schemas request/response
    security.py          # AgentShield & xác thực nội bộ (CSRF/internal token)
    context_engine.py    # Nạp ngữ cảnh đa tầng
    read_tools.py        # Read-tools truy vấn CSDL LibraryHub (SELECT-only)
    reasoning.py         # Phân loại ý định/rủi ro/nhánh xử lý
    write_gate.py        # 4B: checkpoint/snapshot & human-in-the-loop
    llm.py               # Gọi Gemini, retry/fallback, ghi nhận usage
    export_service.py    # Xuất báo cáo .xlsx/.docx/.pdf
    response_formatter.py
    audit.py             # Audit log & AI usage
    session_store.py     # Lịch sử hội thoại/session (file JSON)
    db.py                # Kết nối SQL Server (SQLAlchemy)
  data/
    audit/               # audit-YYYYMMDD.jsonl, ai-usage-YYYYMMDD.jsonl
    checkpoints/         # checkpoint & snapshot tác vụ nhạy cảm
    exports/             # file báo cáo xuất ra
    sessions/            # lịch sử hội thoại theo session
  .env                   # cấu hình thực tế (không commit bí mật)
  .env.example
  requirements.txt
  main.py                # entry point chạy uvicorn
  README.md
```

## Yêu cầu hệ thống

- Python 3.10+ (khuyến nghị 3.11+)
- Nếu dùng kết nối SQL Server qua `pyodbc`: cài **ODBC Driver 17/18 for SQL Server** trên máy chạy Chatbot
- Tài khoản/chuỗi kết nối CSDL có quyền đọc dữ liệu LibraryHub

## Cài đặt

```powershell
cd Chatbot
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
Copy-Item .env.example .env
# Chỉnh sửa .env: DATABASE_URL, GEMINI_API_KEY/MODEL, CHATBOT_INTERNAL_API_KEY
```

## Cấu hình quan trọng (`.env`)

- `CHATBOT_INTERNAL_API_KEY`: khóa bí mật nội bộ. ASP.NET phải gửi header `X-Internal-Token` trùng giá trị này khi proxy request (sau khi đã xác thực user + AntiForgery tại web).
- `DATABASE_URL`: chuỗi kết nối SQLAlchemy tới SQL Server, ví dụ:
  - `mssql+pyodbc:///?odbc_connect=DRIVER%3D%7BODBC+Driver+17+for+SQL+Server%7D%3BSERVER%3DHOST%5CSQLEXPRESS%3BDATABASE%3DLibraryManagement%3BTrusted_Connection%3Dyes%3BTrustServerCertificate%3Dyes`
- `GEMINI_API_KEY`, `GEMINI_MODEL` (hoặc `GEMINI_API_KEYS` JSON để dự phòng)
- `DATA_DIR`, `EXPORT_BASE_URL` (mặc định `/exports`)

Trong môi trường `development`, nếu chưa đặt `CHATBOT_INTERNAL_API_KEY`, service cho phép gọi để tiện kiểm thử; **production bắt buộc cấu hình khóa nội bộ**.

## Chạy dịch vụ

```powershell
cd Chatbot
python main.py
# hoặc
python -m uvicorn app.main:app --host 127.0.0.1 --port 8010 --reload
```

Kiểm tra sức khỏe: `GET /health`

## API tích hợp

### `POST /chat/send`
Request JSON (từ ASP.NET backend đã xác thực):
```json
{
  "message": "Thống kê doanh thu tháng 11/2026 và xuất file Excel",
  "role": "Admin",
  "user_id": 1,
  "user_name": "admin",
  "session_id": "user-1-admin",
  "page_context": "Admin/Statistics",
  "requested_export": "xlsx"
}
```

Header (server-to-server): `X-Internal-Token: <CHATBOT_INTERNAL_API_KEY>`

Response JSON:
```json
{
  "success": true,
  "message": "...Markdown...",
  "intent": "read_lookup",
  "risk_level": "low",
  "requires_human_approval": false,
  "checkpoint_id": null,
  "audit_id": "...",
  "export": {"type":"xlsx","filename":"...xlsx","url":"/exports/...xlsx"},
  "sources": ["LibraryStatistics/Revenue"]
}
```

`message` luôn là Markdown; giao diện hiện tại của LibraryHub dùng trường `message` để hiển thị trong widget.

## Tích hợp với ASP.NET Core MVC (LibraryHub)

Giao diện đã có widget chat tại `Views/Shared/_ChatWidget.cshtml` (gọi `POST /chat/send` của ASP.NET với AntiForgery token) và được include trong layout Patron/Admin.

Cách tích hợp khuyến nghị:
1. Chạy Chatbot Python service (ví dụ `http://127.0.0.1:8010`).
2. Cấu hình ASP.NET để proxy sang Python (giữ nguyên endpoint `/chat/send` và CSRF của ASP.NET):
   - Thêm cấu hình `ChatbotService:BaseUrl` và `ChatbotService:InternalApiKey` (hoặc dùng chung giá trị an toàn qua secret/configuration).
   - Trong `ChatController`/service, sau khi xác thực role và AntiForgery, forward payload kèm `role`, `user_id`, `user_name`, `session_id` sang Python `/chat/send` với header `X-Internal-Token`.
3. Python thực hiện AgentShield, nạp ngữ cảnh (CSDL/lịch sử/quyền), suy luận, read-tools/4B gate, xuất file, audit & AI usage, rồi trả kết quả cho ASP.NET để hiển thị.

Lưu ý bảo mật:
- Không để lộ `CHATBOT_INTERNAL_API_KEY`, `GEMINI_API_KEY`, chuỗi kết nối CSDL ở client.
- ASP.NET là điểm tin cậy xác thực phiên/CSRF; Python kiểm tra token nội bộ và áp dụng AgentShield + kiểm soát quyền theo role/user.
- Tác vụ nhạy cảm phải đi qua 4B (phân quyền, checkpoint/snapshot, human-in-the-loop) và **không** tự động thay đổi trực tiếp CSDL/cấu hình.

## Audit & tuân thủ theo sơ đồ

- Mọi request được ghi nhận: nhận yêu cầu, chặn bảo mật, yêu cầu làm rõ, cổng tác vụ nhạy cảm, hoàn tất/thất bại.
- Mỗi lần gọi mô hình ghi `ai-usage-*.jsonl` (model, độ trễ, số ký tự, trạng thái).
- Checkpoint/snapshot lưu tại `data/checkpoints/` để truy vết phê duyệt human-in-the-loop.

## Ghi chú

- Read-tools hiện triển khai các truy vấn phục vụ nghiệp vụ LibraryHub (sách, thể loại, mượn/trả, thống kê/doanh thu, cảnh báo quá hạn, bài viết, lịch sử mượn cá nhân). Có thể mở rộng thêm truy vấn theo bảng/màn hình cụ thể.
- Nếu `DATABASE_URL` chưa cấu hình, chatbot vẫn chạy nhưng sẽ báo không có dữ liệu CSDL để tra cứu.
- Tính năng xuất PDF dùng font hệ thống (Windows: Arial/Tahoma/Segoe UI nếu có) để hỗ trợ tiếng Việt tốt hơn.
