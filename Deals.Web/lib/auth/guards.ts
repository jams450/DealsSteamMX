import { notFound, redirect } from "next/navigation";
import { getServerSession } from "@/lib/auth/session";

export async function requireAdminSession() {
  const session = await getServerSession();

  if (!session) {
    redirect("/login");
  }

  if ((session.user.role ?? "").toLowerCase() !== "admin") {
    notFound();
  }

  return session;
}
