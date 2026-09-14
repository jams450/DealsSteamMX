import { redirect } from "next/navigation";
import { ProductShell } from "@/components/navigation/product-shell";
import { getServerSession } from "@/lib/auth/session";
import { GameClient } from "./game-client";

interface GamePageProps { readonly params: Promise<{ steamAppId: string }>; }

export default async function GamePage({ params }: GamePageProps) {
  if (!(await getServerSession())) redirect("/login");
  const { steamAppId } = await params;
  return (
    <ProductShell title="Detalle del juego" subtitle="Consulta el precio regional en Steam.">
      <GameClient appId={Number(steamAppId)} />
    </ProductShell>
  );
}
