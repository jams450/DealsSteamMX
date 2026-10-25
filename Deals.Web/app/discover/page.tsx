import { Suspense } from "react";
import { redirect } from "next/navigation";
import { ProductShell } from "@/components/navigation/product-shell";
import { getServerSession } from "@/lib/auth/session";
import { DiscoverClient } from "./discover-client";

export default async function DiscoverPage() {
  const session = await getServerSession();
  if (!session) redirect("/login");

  return (
    <ProductShell
      title="Descubrir"
      subtitle="Menos de $50 MXN, mínimos verificados y mejores ofertas del catálogo conocido."
      meta={<span className="tabler-badge tabler-badge-info">Multitienda · MXN</span>}
    >
      <Suspense fallback={<p className="app-card p-5 text-sm text-muted">Cargando...</p>}>
        <DiscoverClient />
      </Suspense>
    </ProductShell>
  );
}
