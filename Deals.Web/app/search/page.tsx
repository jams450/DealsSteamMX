import { Suspense } from "react";
import { redirect } from "next/navigation";
import { ProductShell } from "@/components/navigation/product-shell";
import { getServerSession } from "@/lib/auth/session";
import { SearchClient } from "./search-client";

export default async function SearchPage() {
  const session = await getServerSession();
  if (!session) redirect("/login");

  return (
    <ProductShell title="Comparar precios" subtitle="Encuentra el mejor precio entre distintas tiendas.">
      <Suspense fallback={<p className="app-card p-5 text-sm text-muted">Cargando...</p>}>
        <SearchClient />
      </Suspense>
    </ProductShell>
  );
}
