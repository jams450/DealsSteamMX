"use client";

import { useCallback, useEffect, useState } from "react";

export type ToastVariant = "success" | "error";

export type Toast = {
  readonly id: string;
  readonly message: string;
  readonly variant: ToastVariant;
};

const TOAST_TTL_MS = 2800;

export function useToasts() {
  const [toasts, setToasts] = useState<Toast[]>([]);

  const dismissToast = useCallback((id: string) => {
    setToasts((current) => current.filter((toast) => toast.id !== id));
  }, []);

  const pushToast = useCallback((message: string, variant: ToastVariant) => {
    const id = crypto.randomUUID();
    setToasts((current) => [...current.slice(-2), { id, message, variant }]);
  }, []);

  const success = useCallback((message: string) => pushToast(message, "success"), [pushToast]);
  const error = useCallback((message: string) => pushToast(message, "error"), [pushToast]);

  useEffect(() => {
    if (toasts.length === 0) return;
    const timeout = window.setTimeout(() => setToasts((current) => current.slice(1)), TOAST_TTL_MS);
    return () => window.clearTimeout(timeout);
  }, [toasts]);

  return { toasts, dismissToast, success, error };
}
