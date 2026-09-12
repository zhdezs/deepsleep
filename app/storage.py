# -*- coding: utf-8 -*-
"""数据存储模块：使用 SQLite 持久化会话、消息与辩论知识库。"""

import os
import sqlite3
import datetime

from . import data_dir

DB_DIR = data_dir()
DB_PATH = os.path.join(DB_DIR, "app.db")


def _now():
    return datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")


class Storage:
    def __init__(self, path=DB_PATH):
        self.path = path
        os.makedirs(os.path.dirname(path), exist_ok=True)
        # check_same_thread=False：允许在多线程（如 Web 服务）中复用连接；
        # 调用方需自行保证串行访问（Web 端已用锁包裹）。
        self.conn = sqlite3.connect(path, check_same_thread=False)
        self.conn.row_factory = sqlite3.Row
        self._init_db()

    def _init_db(self):
        c = self.conn.cursor()
        c.execute(
            """CREATE TABLE IF NOT EXISTS sessions(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                troll_name TEXT DEFAULT '',
                created_at TEXT,
                ended_at TEXT,
                status TEXT DEFAULT 'active')"""
        )
        c.execute(
            """CREATE TABLE IF NOT EXISTS messages(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id INTEGER,
                role TEXT,
                content TEXT,
                category TEXT,
                strategy TEXT,
                aggression INTEGER,
                created_at TEXT)"""
        )
        c.execute(
            """CREATE TABLE IF NOT EXISTS knowledge(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                category TEXT,
                strategy TEXT,
                wins INTEGER DEFAULT 0,
                uses INTEGER DEFAULT 0,
                UNIQUE(category, strategy))"""
        )
        self.conn.commit()

    # ---- 会话 ----
    def create_session(self, troll_name=""):
        c = self.conn.cursor()
        c.execute(
            "INSERT INTO sessions(troll_name, created_at, status) VALUES(?,?,?)",
            (troll_name, _now(), "active"),
        )
        self.conn.commit()
        return c.lastrowid

    def end_session(self, sid):
        self.conn.execute(
            "UPDATE sessions SET ended_at=?, status='ended' WHERE id=?",
            (_now(), sid),
        )
        self.conn.commit()

    def sessions(self):
        cur = self.conn.execute("SELECT * FROM sessions ORDER BY id DESC")
        return [dict(r) for r in cur.fetchall()]

    # ---- 消息 ----
    def add_message(self, sid, role, content, category=None, strategy=None, aggression=None):
        c = self.conn.cursor()
        c.execute(
            """INSERT INTO messages
               (session_id, role, content, category, strategy, aggression, created_at)
               VALUES(?,?,?,?,?,?,?)""",
            (sid, role, content, category, strategy, aggression, _now()),
        )
        self.conn.commit()
        return c.lastrowid

    def session_messages(self, sid):
        cur = self.conn.execute(
            "SELECT * FROM messages WHERE session_id=? ORDER BY id", (sid,)
        )
        return [dict(r) for r in cur.fetchall()]

    def all_messages(self):
        cur = self.conn.execute("SELECT * FROM messages ORDER BY id")
        return [dict(r) for r in cur.fetchall()]

    # ---- 知识库 ----
    def record_knowledge(self, category, strategy, win):
        c = self.conn.cursor()
        c.execute(
            """INSERT INTO knowledge(category, strategy, wins, uses) VALUES(?,?,?,1)
               ON CONFLICT(category, strategy)
               DO UPDATE SET uses=uses+1, wins=wins+?""",
            (category, strategy, 1 if win else 0, 1 if win else 0),
        )
        self.conn.commit()

    def knowledge(self):
        cur = self.conn.execute(
            """SELECT *, CASE WHEN uses>0 THEN wins*1.0/uses ELSE 0 END rate
               FROM knowledge ORDER BY rate DESC, uses DESC"""
        )
        return [dict(r) for r in cur.fetchall()]

    # ---- 统计 ----
    def stats(self):
        cur = self.conn.execute(
            """SELECT COUNT(*) AS total,
                      COUNT(DISTINCT session_id) AS sessions,
                      SUM(CASE WHEN role='troll' THEN 1 ELSE 0 END) AS troll_msgs,
                      SUM(CASE WHEN role='ai' THEN 1 ELSE 0 END) AS ai_msgs
               FROM messages"""
        )
        row = dict(cur.fetchone())
        cur = self.conn.execute(
            "SELECT category, COUNT(*) AS n FROM messages WHERE role='troll' GROUP BY category ORDER BY n DESC"
        )
        row["categories"] = [dict(r) for r in cur.fetchall()]
        cur = self.conn.execute(
            "SELECT strategy, COUNT(*) AS n FROM messages WHERE strategy IS NOT NULL GROUP BY strategy ORDER BY n DESC"
        )
        row["strategies"] = [dict(r) for r in cur.fetchall()]
        return row
