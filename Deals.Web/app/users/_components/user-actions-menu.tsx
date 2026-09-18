import { Button } from "@/components/ui/button";
import { cn } from "@/lib/ui/cn";
import type { AdminUser } from "@/lib/contracts/users-admin";
import { Pencil, Power, Trash2 } from "lucide-react";

type Props = {
  user: AdminUser;
  mobile?: boolean;
  onEdit: (user: AdminUser) => void;
  onToggleActive: (user: AdminUser) => void;
  onDelete: (user: AdminUser) => void;
};

const actionClass = "h-8 gap-1.5 px-2 text-[11px] font-semibold";

export function UserActionsMenu({ user, mobile = false, onEdit, onToggleActive, onDelete }: Props) {
  return (
    <div className={cn("gap-1.5", mobile ? "grid grid-cols-3" : "flex justify-end")} role="group" aria-label={`Acciones para ${user.email}`}>
      <Button
        type="button"
        variant="secondary"
        className={actionClass}
        onClick={() => onEdit(user)}
        aria-label={`Editar usuario ${user.email}`}
      >
        <Pencil className="h-3.5 w-3.5" aria-hidden="true" />
        <span>Editar</span>
      </Button>

      <Button
        type="button"
        variant="secondary"
        className={actionClass}
        onClick={() => onToggleActive(user)}
        aria-label={`${user.active ? "Desactivar" : "Activar"} usuario ${user.email}`}
      >
        <Power className="h-3.5 w-3.5" aria-hidden="true" />
        <span>{user.active ? "Desactivar" : "Activar"}</span>
      </Button>

      <Button
        type="button"
        variant="danger"
        className={actionClass}
        onClick={() => onDelete(user)}
        aria-label={`Borrar usuario ${user.email}`}
      >
        <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />
        <span>Borrar</span>
      </Button>
    </div>
  );
}
