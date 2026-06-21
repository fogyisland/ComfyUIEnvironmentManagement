from __future__ import annotations
from dataclasses import dataclass
from typing import Generic, TypeVar

T = TypeVar("T")

@dataclass
class ServiceError:
    code: str
    message: str
    detail: dict | None = None
    recoverable: bool = True

class Result(Generic[T]):
    """Result type for service operations.

    NOTE: Not using @dataclass here because Python 3.10's dataclass treats the
    classmethod `ok` (defined below) as the default for the `ok` field, causing
    `non-default argument 'value' follows default argument`. Using manual __init__
    preserves both the field name and the classmethod name.
    """
    ok: bool
    value: T | None
    error: ServiceError | None

    def __init__(self, ok: bool, value: T | None, error: ServiceError | None):
        self.ok = ok
        self.value = value
        self.error = error

    @classmethod
    def ok(cls, value: T) -> "Result[T]":
        return cls(ok=True, value=value, error=None)

    @classmethod
    def fail(cls, error: ServiceError) -> "Result[T]":
        return cls(ok=False, value=None, error=error)
