import { notFound, redirect } from "next/navigation";
import { getServerSession } from "@/lib/auth/session";
import { JobsClient } from "./jobs-client";
export default async function JobsPage() { const session = await getServerSession(); if (!session) redirect("/login"); if ((session.user.role ?? "").toLowerCase() !== "admin") notFound(); return <JobsClient username={session.user.username} />; }
