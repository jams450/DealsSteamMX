import { Suspense } from "react";
import { redirect } from "next/navigation";
import { ProductShell } from "@/components/navigation/product-shell";
import { getServerSession } from "@/lib/auth/session";
import { BundlesClient } from "./bundles-client";

export default async function BundlesPage() {
  const session = await getServerSession();
  if (!session) redirect("/login");

  return (
    <ProductShell
      title="Bundles"
      subtitle="Paquetes activos de ITAD con los juegos que incluyen y su reparto estimado por juego."
      meta={<span className="tabler-badge tabler-badge-info">ITAD</span>}
    >
      <Suspense fallback={<p className="app-card p-5 text-sm text-muted">Cargando...</p>}>
        <BundlesClient />
      </Suspense>
    </ProductShell>
  );
}
