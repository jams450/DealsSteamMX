import { ProductShell } from "@/components/navigation/product-shell";
import { requireAdminSession } from "@/lib/auth/guards";
import { LibraryClient } from "./library-client";

export default async function LibraryPage() {
  await requireAdminSession();

  return (
    <ProductShell
      wide
      title="Biblioteca de juegos"
      subtitle="Los juegos de tu export de Playnite, agrupados por tienda. La importación es manual y no toca tu biblioteca en ninguna tienda."
    >
      <LibraryClient />
    </ProductShell>
  );
}
