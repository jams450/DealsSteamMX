"use client";

import { useState } from "react";
import { toUserFormState, type AdminUser, type UserFormErrors, type UserFormState } from "@/lib/contracts/users-admin";

export function useUserForm() {
  const [openForm, setOpenForm] = useState(false);
  const [editing, setEditing] = useState<AdminUser | null>(null);
  const [form, setForm] = useState<UserFormState>(toUserFormState());
  const [formErrors, setFormErrors] = useState<UserFormErrors>({});
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  function openCreate() {
    setEditing(null);
    setForm(toUserFormState());
    setFormErrors({});
    setSubmitError(null);
    setOpenForm(true);
  }

  function openEdit(user: AdminUser) {
    setEditing(user);
    setForm(toUserFormState(user));
    setFormErrors({});
    setSubmitError(null);
    setOpenForm(true);
  }

  function closeForm() {
    setOpenForm(false);
  }

  function onFormChange<K extends keyof UserFormState>(key: K, value: UserFormState[K]) {
    setForm((current) => ({ ...current, [key]: value }));
  }

  return {
    openForm,
    editing,
    form,
    formErrors,
    submitError,
    submitting,
    setFormErrors,
    setSubmitError,
    setSubmitting,
    openCreate,
    openEdit,
    closeForm,
    onFormChange
  };
}
