import { ProductShell } from "@/components/navigation/product-shell";
import { requireAdminSession } from "@/lib/auth/guards";
import { WishlistClient } from "./wishlist-client";

export default async function WishlistPage() {
  await requireAdminSession();

  return (
    <ProductShell
      title="Wishlist de Steam"
      subtitle="Los juegos que sigues en Steam, con su prioridad y la fecha de la última actualización de precios."
    >
      <WishlistClient />
    </ProductShell>
  );
}
