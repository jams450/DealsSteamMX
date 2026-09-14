import { redirect } from "next/navigation";
import { ProductShell } from "@/components/navigation/product-shell";
import { getServerSession } from "@/lib/auth/session";

export default async function HomePage() {
  const session = await getServerSession();
  if (!session) redirect("/login");

  return (
    <ProductShell title="DealExt" subtitle="Encuentra el mejor precio">
      <section className="app-card space-y-3 p-5" aria-label="Búsqueda de juegos">
        <h2 className="text-lg font-semibold text-primary">Buscar juegos</h2>
        <p className="text-sm text-muted">Usa la barra de búsqueda arriba</p>
      </section>
      <div className="app-card p-8 text-center">
        <p className="text-sm text-muted">Página de inicio — contenido próximamente</p>
      </div>
    </ProductShell>
  );
}
