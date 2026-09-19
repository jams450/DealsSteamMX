import { ProductShell } from "@/components/navigation/product-shell";
import { requireAdminSession } from "@/lib/auth/guards";
import { DuplicatesClient } from "./duplicates-client";

export default async function LibraryDuplicatesPage() {
  await requireAdminSession();

  return (
    <ProductShell
      wide
      title="Duplicados del catálogo"
      subtitle="Grupos de juegos canónicos que apuntan al mismo título desde identidades distintas. Fusionar es irreversible: el juego absorbido desaparece."
    >
      <DuplicatesClient />
    </ProductShell>
  );
}
