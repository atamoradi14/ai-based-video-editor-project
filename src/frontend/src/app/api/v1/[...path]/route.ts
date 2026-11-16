import { handleStudio } from "@/lib/hosted";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

async function dispatch(request: Request, context: { params: Promise<{ path: string[] }> }) {
  const { path } = await context.params;
  return handleStudio(request, path);
}

export const GET = dispatch;
export const POST = dispatch;
export const PUT = dispatch;
