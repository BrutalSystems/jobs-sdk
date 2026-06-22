"""Exceptions for the Jobs SDK.

`TransientError` signals to the warm-queued consumer that the failure is
recoverable — the message should be redelivered after the retry TTL. Any
other exception type from a handler is treated as terminal: the consumer
calls finish_run(failed) immediately, no retry."""


class TransientError(Exception):
    """Raise from a warm-queued handler to trigger an AMQP-level retry."""
