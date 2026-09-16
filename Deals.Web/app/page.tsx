import { Search, Tag } from "lucide-react";
import Link from "next/link";
import { redirect } from "next/navigation";
import { ProductShell } from "@/components/navigation/product-shell";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { getServerSession } from "@/lib/auth/session";

const steps = [
  { step: "01", title: "Busca el juego", description: "Escribe el nombre y elige entre las coincidencias encontradas." },
  { step: "02", title: "Abre la ficha", description: "Consulta portada, tipo de producto y región de precio." },
  { step: "03", title: "Revisa la oferta", description: "Compara precio base y precio actual, descuento y fecha de actualización." }
];

export default async function HomePage() {
  const session = await getServerSession();
  if (!session) redirect("/login");

  return (
    <ProductShell title="DealExt" subtitle="Encuentra el precio de tu próximo juego">
      <div className="space-y-4">
        <section className="app-card-accent space-y-4 p-6 md:p-8" aria-label="Buscar un juego">
          <p className="text-xs font-semibold uppercase tracking-widest text-accent">Buscador de ofertas</p>
          <h2 className="max-w-2xl text-2xl font-semibold tracking-tight text-primary md:text-3xl">
            Un juego, un precio claro
          </h2>
          <p className="max-w-2xl text-sm text-secondary md:text-base">
            Busca un título y consulta su precio en Steam para la región de México, con descuento y fecha de
            actualización del dato.
          </p>
          <form role="search" action="/search" method="get" className="flex flex-col gap-2 sm:flex-row sm:items-center">
            <div className="min-w-0 flex-1">
              <Input
                id="home-search"
                name="q"
                type="search"
                aria-label="Buscar un juego"
                placeholder="Ej. Hades, Stardew Valley, Hollow Knight..."
                autoComplete="off"
                className="h-11"
              />
            </div>
            <Button type="submit" className="h-11 shrink-0 px-5">
              <Search className="h-4 w-4" aria-hidden="true" />
              Buscar
            </Button>
          </form>
        </section>

        <section className="grid gap-3 md:grid-cols-3" aria-labelledby="home-steps-title">
          <h2 id="home-steps-title" className="sr-only">
            Cómo funciona DealExt
          </h2>
          {steps.map((item) => (
            <article key={item.step} className="app-card space-y-2 p-5">
              <p className="text-xs font-semibold tracking-widest text-muted">{item.step}</p>
              <h3 className="text-base font-semibold text-primary">{item.title}</h3>
              <p className="text-sm text-muted">{item.description}</p>
            </article>
          ))}
        </section>

        <section className="app-card flex flex-wrap items-center gap-3 p-5" aria-label="Fuente de precios">
          <span className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-accent bg-[var(--color-accent-soft)] text-accent">
            <Tag className="h-4 w-4" aria-hidden="true" />
          </span>
          <p className="min-w-0 flex-1 text-sm text-secondary">
            Fuente de precios actual: <strong className="text-primary">Steam</strong>, región México. Cada ficha
            indica cuándo se observó el precio.
          </p>
          <Link href="/search" className="btn-secondary-semantic inline-flex h-10 items-center px-4 text-sm font-semibold">
            Ver buscador
          </Link>
        </section>
      </div>
    </ProductShell>
  );
}
