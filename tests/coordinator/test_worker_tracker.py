from coordinator.worker_tracker import WorkerTracker


def test_init_worker_sets_free():
    t = WorkerTracker()
    t.init_worker("rtx")
    assert t.status("rtx") == "free"
    assert t.is_free("rtx")
    assert t.elapsed_seconds("rtx") == 0.0


def test_init_worker_idempotent():
    t = WorkerTracker()
    t.init_worker("rtx")
    t.init_worker("rtx")  # no error
    assert t.status("rtx") == "free"


def test_acquire_sets_role():
    t = WorkerTracker()
    t.init_worker("rtx")
    assert t.acquire("rtx", "prefill")
    assert t.status("rtx") == "prefill"
    assert not t.is_free("rtx")


def test_acquire_free_workers():
    t = WorkerTracker()
    t.init_worker("rtx")
    t.init_worker("p100")
    t.acquire("rtx", "prefill")
    assert t.free_workers() == ["p100"]
    assert t.busy_workers() == ["rtx"]


def test_acquire_returns_false_when_busy():
    t = WorkerTracker()
    t.init_worker("rtx")
    t.acquire("rtx", "prefill")
    assert not t.acquire("rtx", "decode")


def test_acquire_returns_false_when_unhealthy():
    t = WorkerTracker()
    t.init_worker("rtx")
    t.mark_unhealthy("rtx")
    assert not t.acquire("rtx", "decode")


def test_acquire_returns_false_when_not_initialized():
    t = WorkerTracker()
    assert not t.acquire("unknown", "decode")


def test_release_after_acquire():
    t = WorkerTracker()
    t.init_worker("rtx")
    t.acquire("rtx", "decode")
    t.release("rtx")
    assert t.status("rtx") == "free"
    assert t.is_free("rtx")
    assert t.elapsed_seconds("rtx") == 0.0


def test_release_idempotent():
    t = WorkerTracker()
    t.init_worker("rtx")
    t.release("rtx")  # no error
    assert t.status("rtx") == "free"


def test_on_error_increments():
    t = WorkerTracker()
    t.init_worker("rtx")
    t.on_error("rtx")
    assert t.is_free("rtx")  # still free, errors < threshold


def test_on_error_crosses_threshold():
    t = WorkerTracker(_error_threshold=3)
    t.init_worker("rtx")
    assert t.is_free("rtx")
    t.on_error("rtx")
    t.on_error("rtx")
    t.on_error("rtx")
    assert not t.is_free("rtx")


def test_on_success_resets_errors():
    t = WorkerTracker(_error_threshold=3)
    t.init_worker("rtx")
    t.on_error("rtx")
    t.on_error("rtx")
    t.on_success("rtx")
    # After reset, 2 more errors should still leave it healthy
    t.on_error("rtx")
    t.on_error("rtx")
    assert t.is_free("rtx")


def test_on_success_clears_errors():
    t = WorkerTracker(_error_threshold=3)
    t.init_worker("rtx")
    t.on_error("rtx")
    t.on_error("rtx")
    t.on_success("rtx")
    t.on_error("rtx")
    assert t.is_free("rtx")


def test_mark_unhealthy():
    t = WorkerTracker()
    t.init_worker("rtx")
    t.mark_unhealthy("rtx")
    assert not t.is_free("rtx")


def test_mark_healthy_resets_errors():
    t = WorkerTracker(_error_threshold=3)
    t.init_worker("rtx")
    t.on_error("rtx")
    t.on_error("rtx")
    t.on_error("rtx")
    assert not t.is_free("rtx")
    t.mark_healthy("rtx")
    assert t.is_free("rtx")


def test_on_success_does_not_release():
    t = WorkerTracker()
    t.init_worker("rtx")
    t.acquire("rtx", "decode")
    t.on_success("rtx")
    assert not t.is_free("rtx")  # on_success only resets errors, does not release
    assert t.status("rtx") == "decode"


def test_free_workers_excludes_unhealthy():
    t = WorkerTracker()
    t.init_worker("rtx")
    t.init_worker("p100")
    t.mark_unhealthy("rtx")
    assert t.free_workers() == ["p100"]


def test_free_workers_excludes_busy():
    t = WorkerTracker()
    t.init_worker("rtx")
    t.init_worker("p100")
    t.acquire("rtx", "decode")
    assert t.free_workers() == ["p100"]


def test_free_workers_unknown_not_included():
    t = WorkerTracker()
    t.init_worker("rtx")
    assert t.free_workers() == ["rtx"]


def test_busy_since_returns_none_when_free():
    t = WorkerTracker()
    t.init_worker("rtx")
    assert t.busy_since("rtx") is None


def test_busy_since_returns_time_after_acquire():
    import time
    t = WorkerTracker()
    t.init_worker("rtx")
    t.acquire("rtx", "decode")
    since = t.busy_since("rtx")
    assert since is not None
    assert since > 0


def test_elapsed_seconds_grows():
    import time
    t = WorkerTracker()
    t.init_worker("rtx")
    t.acquire("rtx", "decode")
    before = t.elapsed_seconds("rtx")
    time.sleep(0.01)
    after = t.elapsed_seconds("rtx")
    assert after > before


def test_all_workers():
    t = WorkerTracker()
    t.init_worker("rtx")
    t.init_worker("p100")
    assert set(t.all_workers) == {"rtx", "p100"}


def test_status_unknown():
    t = WorkerTracker()
    assert t.status("nonexistent") == "unknown"


def test_is_expired_false_when_free():
    t = WorkerTracker()
    t.init_worker("rtx")
    assert not t.is_expired("rtx")


def test_is_expired_false_below_threshold():
    t = WorkerTracker()
    t.init_worker("rtx")
    t.acquire("rtx", "decode")
    # elapsed is near 0, well below 600s default
    assert not t.is_expired("rtx")


def test_is_expired_true_with_low_threshold():
    import time
    t = WorkerTracker()
    t.init_worker("rtx")
    t.acquire("rtx", "decode")
    time.sleep(0.01)
    assert t.is_expired("rtx", max_seconds=0.005)


def test_is_expired_custom_threshold():
    t = WorkerTracker()
    t.init_worker("rtx")
    assert not t.is_expired("rtx", max_seconds=3600)


def test_acquire_after_on_success_on_error_threshold():
    t = WorkerTracker(_error_threshold=3)
    t.init_worker("rtx")
    t.acquire("rtx", "decode")
    t.on_success("rtx")
    t.release("rtx")
    assert t.is_free("rtx")
    t.on_error("rtx")
    t.on_error("rtx")
    t.on_error("rtx")
    assert not t.is_free("rtx")
