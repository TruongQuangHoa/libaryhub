from __future__ import annotations

import re
from datetime import datetime
from typing import Any

from .db import execute_read_query


def _fmt_rows(rows: list[dict[str, Any]], limit: int = 20) -> str:
    if not rows:
        return "(Không có dữ liệu)"
    shown = rows[:limit]
    lines = []
    for i, r in enumerate(shown, start=1):
        parts = []
        for k, v in r.items():
            if hasattr(v, "isoformat"):
                try:
                    v = v.isoformat(sep=" ", timespec="seconds")
                except Exception:
                    v = str(v)
            parts.append(f"{k}={v}")
        lines.append(f"{i}. " + "; ".join(parts))
    if len(rows) > limit:
        lines.append(f"... và {len(rows) - limit} dòng khác")
    return "\n".join(lines)


def _has_keyword(message: str, keywords: tuple[str, ...]) -> bool:
    m = (message or "").lower()
    return any(k in m for k in keywords)


def _extract_year(message: str) -> int | None:
    m = re.search(r"\b(20\d{2})\b", message or "")
    if not m:
        return None
    try:
        return int(m.group(1))
    except Exception:
        return None


def _extract_month(message: str) -> int | None:
    m = re.search(r"(?:tháng|thang|month)\s*([1-9]|1[0-2])\b", (message or "").lower())
    if not m:
        m = re.search(r"\b([1-9]|1[0-2])\s*/\s*(20\d{2})\b", message or "")
        if m:
            try:
                return int(m.group(1))
            except Exception:
                return None
        return None
    try:
        return int(m.group(1))
    except Exception:
        return None


def search_books(message: str) -> str:
    text = (message or "").strip()
    term = text
    for kw in ("tìm sách", "tìm", "tra cứu sách", "sách", "book", "tác giả", "nhà xuất bản", "thể loại", "xin", "cho tôi", "giúp", "về"):
        term = re.sub(rf"(?i)\b{re.escape(kw)}\b", " ", term)
    term = re.sub(r"\s+", " ", term).strip(" :?-–—")
    if len(term) < 2:
        sql = """
        SELECT TOP 10 BookID, TitleBook, Author, Publisher, PublicationYear, CategoryID,
               BookQuantity, BorrowQuantity, IsActive,
               CASE WHEN BookQuantity > BorrowQuantity THEN (BookQuantity - BorrowQuantity) ELSE 0 END AS AvailableQuantity
        FROM tblBook
        ORDER BY TotalReaders DESC, BookID DESC
        """
        rows, err = execute_read_query(sql)
        if err:
            return f"[Sách] {err}"
        return "[Sách nổi bật/gần đây]\n" + _fmt_rows(rows)
    like = f"%{term}%"
    sql = """
    SELECT TOP 15 BookID, TitleBook, Author, Publisher, PublicationYear, CategoryID,
           BookQuantity, BorrowQuantity, IsActive,
           CASE WHEN BookQuantity > BorrowQuantity THEN (BookQuantity - BorrowQuantity) ELSE 0 END AS AvailableQuantity
    FROM tblBook
    WHERE TitleBook LIKE :term OR Author LIKE :term OR Publisher LIKE :term
    ORDER BY IsActive DESC, TotalReaders DESC, BookID DESC
    """
    rows, err = execute_read_query(sql, {"term": like})
    if err:
        return f"[Tra cứu sách] {err}"
    return f"[Kết quả tra cứu sách cho '{term}']\n" + _fmt_rows(rows)


def get_categories() -> str:
    sql = """
    SELECT c.CategoryID, c.CategoryName, c.IsActive,
           COUNT(b.BookID) AS BookCount,
           ISNULL(SUM(b.BookQuantity), 0) AS TotalQuantity
    FROM tblCategory c
    LEFT JOIN tblBook b ON b.CategoryID = c.CategoryID
    GROUP BY c.CategoryID, c.CategoryName, c.IsActive
    ORDER BY c.CategoryName
    """
    rows, err = execute_read_query(sql)
    if err:
        return f"[Thể loại] {err}"
    return "[Danh mục thể loại]\n" + _fmt_rows(rows, limit=50)


def get_statistics_summary() -> str:
    parts = []
    for label, sql in [
        ("Tổng đầu sách", "SELECT COUNT(*) AS TotalBooks FROM tblBook"),
        ("Tổng bản sách (số lượng)", "SELECT ISNULL(SUM(BookQuantity),0) AS TotalCopies FROM tblBook"),
        ("Độc giả đang hoạt động", "SELECT COUNT(*) AS ActivePatrons FROM tblPatron WHERE IsActive = 1"),
        ("Tổng phiếu mượn", "SELECT COUNT(*) AS TotalBorrowings FROM tblBorrowing"),
        ("Phiếu mượn đang mở (chưa trả)", "SELECT COUNT(*) AS OpenBorrowings FROM tblBorrowing WHERE IsActive = 1"),
        ("Tổng phiếu trả", "SELECT COUNT(*) AS TotalReturns FROM tblReturn"),
        ("Tổng thể loại", "SELECT COUNT(*) AS TotalCategories FROM tblCategory"),
    ]:
        rows, err = execute_read_query(sql)
        if err:
            return f"[Thống kê] {err}"
        parts.append(f"- {label}: {list(rows[0].values())[0] if rows else 'N/A'}")

    rows, err = execute_read_query(
        """
        SELECT TOP 5 TitleBook, Author, BorrowQuantity, BookQuantity,
               CASE WHEN BookQuantity > BorrowQuantity THEN (BookQuantity - BorrowQuantity) ELSE 0 END AS AvailableQuantity
        FROM tblBook
        ORDER BY BorrowQuantity DESC, BookID DESC
        """
    )
    if err:
        return f"[Thống kê] {err}"
    parts.append("\nTop sách mượn nhiều:\n" + _fmt_rows(rows, limit=5))
    return "[Tổng quan thống kê thư viện]\n" + "\n".join(parts)


def get_revenue_borrowing(message: str) -> str:
    year = _extract_year(message) or datetime.now().year
    month = _extract_month(message)
    params: dict[str, Any] = {"year": year}
    where_borrow = "BorrowDate IS NOT NULL AND YEAR(BorrowDate) = :year"
    where_return = "ReturnDate IS NOT NULL AND YEAR(ReturnDate) = :year"
    period_label = f"năm {year}"
    if month:
        params["month"] = month
        where_borrow += " AND MONTH(BorrowDate) = :month"
        where_return += " AND MONTH(ReturnDate) = :month"
        period_label = f"tháng {month}/{year}"

    sql = f"""
    SELECT
        COUNT(*) AS TotalBorrowings,
        ISNULL(SUM(TotalBorrowingFee), 0) AS BorrowingRevenue
    FROM tblBorrowing
    WHERE {where_borrow}
    """
    rows, err = execute_read_query(sql, params)
    if err:
        return f"[Doanh thu/Lượt mượn] {err}"
    sql_fine = f"""
    SELECT ISNULL(SUM(TotalFine), 0) AS FineRevenue, COUNT(*) AS TotalReturns
    FROM tblReturn
    WHERE {where_return}
    """
    fine_rows, fine_err = execute_read_query(sql_fine, params)
    if fine_err:
        return f"[Doanh thu/Lượt mượn] {fine_err}"
    b = rows[0] if rows else {}
    f = fine_rows[0] if fine_rows else {}
    borrow_rev = float(b.get("BorrowingRevenue") or 0)
    fine_rev = float(f.get("FineRevenue") or 0)
    return (
        f"[Thống kê mượn/trả & doanh thu {period_label}]\n"
        f"- Tổng lượt mượn: {b.get('TotalBorrowings', 0)}\n"
        f"- Doanh thu phí mượn: {borrow_rev:,.0f}\n"
        f"- Tổng phiếu trả: {f.get('TotalReturns', 0)}\n"
        f"- Doanh thu tiền phạt: {fine_rev:,.0f}\n"
        f"- Tổng doanh thu (ước tính): {borrow_rev + fine_rev:,.0f}\n"
        "(Số liệu tổng hợp từ CSDL thực tế theo điều kiện thời gian truy vấn.)"
    )


def get_overdue_alerts(role: str, user_id: int | None = None) -> str:
    if role == "Patron":
        return get_my_overdue_borrowings(user_id)

    sql = """
    SELECT TOP 20 b.BorrowingID, b.PatronID, p.PatronName, b.BorrowDate, b.DueDate,
           DATEDIFF(DAY, b.DueDate, GETDATE()) AS OverdueDays,
           b.TotalBorrowingFee
    FROM tblBorrowing b
    LEFT JOIN tblPatron p ON b.PatronID = p.PatronID
    WHERE b.IsActive = 1 AND b.DueDate IS NOT NULL AND b.DueDate < GETDATE()
    ORDER BY b.DueDate ASC
    """
    rows, err = execute_read_query(sql)
    if err:
        return f"[Cảnh báo quá hạn] {err}"
    return "[Cảnh báo phiếu mượn quá hạn (toàn hệ thống)]\n" + _fmt_rows(rows)


def get_my_overdue_borrowings(user_id: int | None) -> str:
    if not user_id:
        return "[Quá hạn của tôi] Không xác định được tài khoản người dùng để tra cứu dữ liệu cá nhân."
    sql = """
    SELECT TOP 20 b.BorrowingID, b.BorrowDate, b.DueDate, b.TotalBorrowingFee,
           DATEDIFF(DAY, b.DueDate, GETDATE()) AS OverdueDays
    FROM tblBorrowing b
    INNER JOIN tblPatron p ON b.PatronID = p.PatronID
    WHERE p.UserID = :user_id
      AND b.IsActive = 1
      AND b.DueDate IS NOT NULL
      AND b.DueDate < GETDATE()
    ORDER BY b.DueDate ASC
    """
    rows, err = execute_read_query(sql, {"user_id": user_id})
    if err:
        return f"[Quá hạn của tôi] {err}"
    if not rows:
        return "[Quá hạn của tôi] Bạn hiện không có phiếu mượn nào quá hạn."
    return "[Phiếu mượn quá hạn của bạn]\n" + _fmt_rows(rows)


def get_patron_borrowings(user_id: int | None) -> str:
    if not user_id:
        return "[Phiếu mượn cá nhân] Không xác định được tài khoản người dùng để tra cứu dữ liệu cá nhân."
    sql = """
    SELECT TOP 20 b.BorrowingID, b.BorrowDate, b.DueDate, b.TotalBorrowingFee, b.IsActive,
           CASE
             WHEN b.IsActive = 0 THEN N'Đã trả'
             WHEN b.DueDate IS NOT NULL AND b.DueDate < GETDATE() THEN N'Quá hạn'
             ELSE N'Đang mượn'
           END AS Status,
           CASE
             WHEN b.IsActive = 1 AND b.DueDate IS NOT NULL AND b.DueDate < GETDATE()
               THEN DATEDIFF(DAY, b.DueDate, GETDATE())
             ELSE 0
           END AS OverdueDays
    FROM tblBorrowing b
    INNER JOIN tblPatron p ON b.PatronID = p.PatronID
    WHERE p.UserID = :user_id
    ORDER BY b.BorrowDate DESC, b.BorrowingID DESC
    """
    rows, err = execute_read_query(sql, {"user_id": user_id})
    if err:
        return f"[Phiếu mượn cá nhân] {err}"
    return "[Lịch sử/phiếu mượn của bạn]\n" + _fmt_rows(rows)


def get_patron_profile_summary(user_id: int | None) -> str:
    if not user_id:
        return "[Hồ sơ độc giả] Không xác định được tài khoản người dùng."
    sql = """
    SELECT TOP 1 p.PatronID, p.PatronName, p.Email, p.Phone, p.RegisterDate, p.IsActive
    FROM tblPatron p
    WHERE p.UserID = :user_id
    """
    rows, err = execute_read_query(sql, {"user_id": user_id})
    if err:
        return f"[Hồ sơ độc giả] {err}"
    return "[Thông tin độc giả (tài khoản hiện tại)]\n" + _fmt_rows(rows, limit=1)


def get_recent_posts() -> str:
    sql = """
    SELECT TOP 10 PostID, Title, Category, IsPublished, IsActive, PublishedDate, ViewCount
    FROM tblPost
    ORDER BY ISNULL(PublishedDate, CreatedDate) DESC, PostID DESC
    """
    rows, err = execute_read_query(sql)
    if err:
        return f"[Bài viết/Tin tức] {err}"
    return "[Bài viết/tin tức gần đây]\n" + _fmt_rows(rows)


def collect_read_context(message: str, role: str, user_id: int | None) -> tuple[str, list[str]]:
    """4A. Điều phối Read-Tools dựa trên yêu cầu và vai trò."""
    msg = message or ""
    sections: list[str] = []
    sources: list[str] = []

    is_personal = _has_keyword(msg, ("của tôi", "cá nhân", "tài khoản của tôi", "lịch sử của tôi", "tôi đang", "tôi đã"))

    if _has_keyword(msg, ("sách", "book", "tác giả", "nhà xuất bản", "tìm", "tra cứu", "tựa", "tên sách")):
        sections.append(search_books(msg))
        sources.append("tblBook")

    if _has_keyword(msg, ("thể loại", "danh mục", "category")):
        sections.append(get_categories())
        sources.append("tblCategory")

    if _has_keyword(msg, ("thống kê", "doanh thu", "lượt mượn", "báo cáo", "revenue", "mượn trả", "phạt", "tổng quan")):
        if role in {"Admin", "Librarian"} or _has_keyword(msg, ("tổng quan", "thống kê")):
            sections.append(get_statistics_summary())
            sources.append("LibraryStatistics")
        if _has_keyword(msg, ("doanh thu", "phạt", "revenue", "lượt mượn", "mượn trả", "báo cáo")) and role in {"Admin", "Librarian"}:
            sections.append(get_revenue_borrowing(msg))
            sources.append("LibraryStatistics/Revenue")

    if _has_keyword(msg, ("quá hạn", "overdue", "trễ hạn", "cảnh báo", "alert")):
        if role == "Patron" or is_personal:
            sections.append(get_my_overdue_borrowings(user_id))
            sources.append("tblBorrowing(my-overdue)")
        else:
            sections.append(get_overdue_alerts(role, user_id))
            sources.append("tblBorrowing(overdue)")

    if _has_keyword(msg, ("mượn", "phiếu mượn", "lịch sử mượn", "đang mượn", "đã trả", "borrow")):
        if role == "Patron":
            sections.append(get_patron_borrowings(user_id))
            sources.append("tblBorrowing(tblPatron.UserID)")
        elif is_personal:
            # Librarian/Admin hỏi về "của tôi" không map trực tiếp tới Patron; bỏ qua tránh lộ sai phạm vi
            pass

    if role == "Patron" and _has_keyword(msg, ("hồ sơ", "thông tin của tôi", "tài khoản", "profile")):
        sections.append(get_patron_profile_summary(user_id))
        sources.append("tblPatron(profile)")

    if _has_keyword(msg, ("bài viết", "tin tức", "post", "thông báo", "blog")):
        sections.append(get_recent_posts())
        sources.append("tblPost")

    # Mặc định cho admin/librarian: nạp tổng quan nhẹ nếu chưa có ngữ cảnh cụ thể
    if not sections and role in {"Admin", "Librarian"}:
        sections.append(get_statistics_summary())
        sources.append("LibraryStatistics")

    # Mặc định cho patron: tra cứu sách + (nếu liên quan mượn/trả) lịch sử cá nhân
    if not sections and role == "Patron":
        sections.append(search_books(msg))
        if _has_keyword(msg, ("mượn", "trả", "hạn")):
            sections.append(get_patron_borrowings(user_id))
            sources.append("tblBorrowing(tblPatron.UserID)")
        sources.append("tblBook")

    uniq_sources = []
    for s in sources:
        if s and s not in uniq_sources:
            uniq_sources.append(s)
    return "\n\n".join(sections).strip(), uniq_sources
