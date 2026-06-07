from coordinator.session_table import SessionTable


def test_register_and_lookup():
    table = SessionTable()
    entry = table.register("sess_abc", "rtx", 0, n_past=100)

    assert entry.session_id == "sess_abc"
    assert entry.node_name == "rtx"
    assert entry.slot_id == 0
    assert entry.n_past == 100

    found = table.lookup("sess_abc")
    assert found is not None
    assert found.session_id == "sess_abc"


def test_lookup_nonexistent():
    table = SessionTable()
    assert table.lookup("nonexistent") is None


def test_update_last_used():
    import time
    table = SessionTable()
    table.register("sess_abc", "rtx", 0)
    entry = table.lookup("sess_abc")
    old = entry.last_used
    time.sleep(0.01)
    table.update_last_used("sess_abc")
    assert entry.last_used > old


def test_mark_evicted():
    table = SessionTable()
    table.register("sess_abc", "rtx", 0)
    table.mark_evicted("sess_abc")

    entry = table.lookup("sess_abc")
    assert entry.slot_freed is True
    assert entry.slot_id == 0
    assert entry.has_store_state is True


def test_get_sessions_on_node():
    table = SessionTable()
    table.register("sess_a", "rtx", 0)
    table.register("sess_b", "p100", 1)
    table.register("sess_c", "rtx", 1)

    rtx_sessions = table.get_sessions_on_node("rtx")
    assert len(rtx_sessions) == 2
    assert all(s.node_name == "rtx" for s in rtx_sessions)


def test_lru_session():
    import time
    table = SessionTable()
    table.register("sess_a", "rtx", 0)
    time.sleep(0.02)
    table.register("sess_b", "rtx", 1)
    time.sleep(0.02)
    table.register("sess_c", "p100", 0)

    lru = table.get_lru_session("rtx")
    assert lru.session_id == "sess_a"


def test_lru_empty_node():
    table = SessionTable()
    assert table.get_lru_session("nonexistent") is None


def test_remove():
    table = SessionTable()
    table.register("sess_abc", "rtx", 0)
    assert table.lookup("sess_abc") is not None
    table.remove("sess_abc")
    assert table.lookup("sess_abc") is None


def test_active_count():
    table = SessionTable()
    assert table.active_count == 0
    table.register("sess_a", "rtx", 0)
    assert table.active_count == 1
    table.register("sess_b", "p100", 0)
    assert table.active_count == 2
    table.remove("sess_a")
    assert table.active_count == 1
