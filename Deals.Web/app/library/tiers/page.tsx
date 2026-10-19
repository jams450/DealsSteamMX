import { ProductShell } from "@/components/navigation/product-shell";
import { requireAdminSession } from "@/lib/auth/guards";
import { TierListClient } from "./tier-list-client";

export default async function TierListPage() {
  await requireAdminSession();
  return <ProductShell title="Tier list anual" subtitle="Clasifica tus juegos reseñados por año. El año corresponde al mes de fin de la partida, o al de inicio si no hay fin."><TierListClient /></ProductShell>;
}
