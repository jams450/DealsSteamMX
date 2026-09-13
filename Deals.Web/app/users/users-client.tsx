"use client";

import { useEffect, useState } from "react";
import { AdminShell } from "@/components/navigation/admin-shell";
import { type AdminUser, type UserCreatePayload, type UserUpdatePayload, validateUserForm } from "@/lib/contracts/users-admin";
import { UserDeleteConfirmDialog } from "./_components/user-delete-confirm-dialog";
import { useUserForm } from "./_hooks/use-user-form";
import { useUsersAdmin } from "./_hooks/use-users-admin";
import { useUsersFilters } from "./_hooks/use-users-filters";
import { UsersToastStack } from "./_components/users-toast-stack";
import { UserFormDrawer } from "./_components/user-form-drawer";
import { UsersResults } from "./_components/users-results";
import { UsersToolbar } from "./_components/users-toolbar";
import { useUsersToasts } from "./_hooks/use-users-toasts";

type Props = { username: string };

export function UsersClient({ username }: Props) {
  const { users, loading, error, setError, create, update, toggleActive: toggleUserActive, remove } = useUsersAdmin();
  const { search, status, role, setSearch, setStatus, setRole, filteredUsers, hasActiveFilters, resetFilters } = useUsersFilters(users);
  const {
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
  } = useUserForm();

  const { toasts, dismissToast, success, error: errorToast } = useUsersToasts();
  const [pendingDeleteUser, setPendingDeleteUser] = useState<AdminUser | null>(null);
  const [deleting, setDeleting] = useState(false);

  useEffect(() => {
    if (!error) {
      return;
    }

    errorToast(error);
    setError(null);
  }, [error, errorToast, setError]);

  async function saveUser() {
    const validation = validateUserForm(form, Boolean(editing));
    setFormErrors(validation);
    if (Object.keys(validation).length > 0) return;

    setSubmitting(true);
    setSubmitError(null);

    const payload: UserCreatePayload | UserUpdatePayload = {
      name: form.name.trim(),
      email: form.email.trim().toLowerCase(),
      ...(form.password.trim() ? { password: form.password.trim() } : {}),
      active: form.active,
      admin: form.admin
    };

    try {
      if (editing) {
        await update(editing.userId, payload);
      } else {
        await create(payload as UserCreatePayload);
      }

      closeForm();
      success(editing ? "Usuario actualizado" : "Usuario creado");
    } catch (err) {
      setSubmitError(err instanceof Error ? err.message : "No se pudo guardar usuario");
    } finally {
      setSubmitting(false);
    }
  }

  async function toggleActive(user: AdminUser) {
    try {
      await toggleUserActive(user);
      success(user.active ? "Usuario desactivado" : "Usuario activado");
    } catch (err) {
      errorToast(err instanceof Error ? err.message : "No se pudo actualizar estado");
    }
  }

  function askDeleteUser(user: AdminUser) {
    setPendingDeleteUser(user);
  }

  async function confirmDeleteUser() {
    if (!pendingDeleteUser) {
      return;
    }

    setDeleting(true);
    try {
      await remove(pendingDeleteUser.userId);
      success("Usuario borrado");
      setPendingDeleteUser(null);
    } catch (err) {
      errorToast(err instanceof Error ? err.message : "No se pudo borrar usuario");
    } finally {
      setDeleting(false);
    }
  }

  return (
    <AdminShell
      username={username}
      section="Administración"
      title="Usuarios"
    >
      <UsersToastStack toasts={toasts} onDismiss={dismissToast} />

      <section className="space-y-2 md:space-y-2">
        <section className="overflow-hidden px-4 py-3 sm:px-5">
          <UsersToolbar
            total={users.length}
            filtered={filteredUsers.length}
            search={search}
            status={status}
            role={role}
            hasActiveFilters={hasActiveFilters}
            onSearchChange={setSearch}
            onStatusChange={setStatus}
            onRoleChange={setRole}
            onResetFilters={resetFilters}
            onCreate={openCreate}
          />
        </section>

        <UsersResults rows={filteredUsers} loading={loading} errorMessage={error} onEdit={openEdit} onToggleActive={toggleActive} onDelete={askDeleteUser} />

        <UserFormDrawer
          open={openForm}
          user={editing}
          form={form}
          errors={formErrors}
          submitError={submitError}
          submitting={submitting}
          onClose={closeForm}
          onChange={onFormChange}
          onSubmit={() => void saveUser()}
        />

        <UserDeleteConfirmDialog
          user={pendingDeleteUser}
          open={Boolean(pendingDeleteUser)}
          loading={deleting}
          onCancel={() => setPendingDeleteUser(null)}
          onConfirm={() => void confirmDeleteUser()}
        />
      </section>
    </AdminShell>
  );
}
