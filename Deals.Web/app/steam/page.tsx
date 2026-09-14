import { redirect } from "next/navigation";
import { getServerSession } from "@/lib/auth/session";
import { SteamClient } from "./steam-client";

export default async function SteamPage() {
  const session = await getServerSession();
  if (!session) redirect("/login");

  return <SteamClient username={session.user.username} />;
}
