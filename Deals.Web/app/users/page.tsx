import { notFound, redirect } from "next/navigation";
import { getServerSession } from "@/lib/auth/session";
import { UsersClient } from "./users-client";

export default async function UsersPage() {
  const session = await getServerSession();
  if (!session) redirect("/login");

  if ((session.user.role ?? "").toLowerCase() !== "admin") {
    notFound();
  }

  return <UsersClient username={session.user.username} />;
}
