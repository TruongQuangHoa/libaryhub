from __future__ import annotations

import logging
from contextlib import contextmanager
from typing import Any, Iterator

from sqlalchemy import create_engine, text
from sqlalchemy.engine import Engine, Result

from .config import get_settings

logger = logging.getLogger(__name__)

_engine: Engine | None = None
_engine_url: str | None = None


def get_engine() -> Engine | None:
    """Tạo/lấy SQLAlchemy engine cho CSDL LibraryHub.

    Trả về None nếu chưa cấu hình DATABASE_URL để service vẫn khởi động được
    (các read-tools sẽ báo chưa có kết nối CSDL).
    """
    global _engine, _engine_url
    settings = get_settings()
    url = (settings.database_url or "").strip()
    if not url:
        return None
    if _engine is not None and _engine_url == url:
        return _engine
    try:
        _engine = create_engine(
            url,
            pool_pre_ping=True,
            pool_recycle=1800,
            future=True,
        )
        _engine_url = url
        return _engine
    except Exception:
        logger.exception("Không thể khởi tạo kết nối CSDL từ DATABASE_URL.")
        _engine = None
        _engine_url = None
        return None


@contextmanager
def db_connection() -> Iterator[Any]:
    engine = get_engine()
    if engine is None:
        yield None
        return
    with engine.connect() as conn:
        yield conn


def execute_read_query(query: str, params: dict[str, Any] | None = None) -> tuple[list[dict[str, Any]], str | None]:
    """Thực thi truy vấn đọc (SELECT) và trả về (rows, error)."""
    engine = get_engine()
    if engine is None:
        return [], "Chưa cấu hình kết nối CSDL (DATABASE_URL)."
    sql = query.strip().strip(";")
    lowered = sql.lower()
    if not lowered.startswith("select") and not lowered.startswith("with"):
        return [], "Chỉ cho phép truy vấn đọc (SELECT/WITH)."
    # Chặn từ khóa nguy hiểm trong read-tools
    forbidden = ("insert", "update", "delete", "drop", "alter", "create", "truncate", "exec", "merge")
    for token in forbidden:
        if f" {token} " in f" {lowered} " or lowered.startswith(token + " "):
            return [], "Truy vấn chứa thao tác không được phép trong chế độ đọc."
    try:
        with engine.connect() as conn:
            result: Result = conn.execute(text(sql), params or {})
            rows = [dict(r._mapping) for r in result.fetchall()]
        return rows, None
    except Exception as exc:
        logger.exception("Lỗi truy vấn đọc CSDL: %s", exc)
        return [], f"Lỗi truy vấn CSDL: {type(exc).__name__}"
