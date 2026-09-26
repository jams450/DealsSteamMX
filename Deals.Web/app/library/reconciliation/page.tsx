import { ProductShell } from "@/components/navigation/product-shell";
import { requireAdminSession } from "@/lib/auth/guards";
import { ReconciliationClient } from "./reconciliation-client";

export default async function ReconciliationPage() {
  await requireAdminSession();
  return (
    <ProductShell wide title="Reconciliación cross-state" subtitle="Candidatos read-only. El título propone; nunca prueba identidad. Cada fusión requiere superviviente explícito y confirmación.">
      <ReconciliationClient />
    </ProductShell>
  );
}
