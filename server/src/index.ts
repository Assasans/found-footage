export interface Env {
  SOURCES_URL: string;
  CONTACT_EMAIL: string;
  CLIENT_VERSION: string;

  DB: D1Database;
  STORAGE: R2Bucket;
}

interface SeqResponse {
  seq: number;
}

interface VideoResponse {
  id: number;
  video_id: string;
  user_id: string;
  language: string;
  reason: string;
  object: string;
  available: boolean;
  ip: string;
  timestamp: string;
}

export default {
  async fetch(request: Request, env: Env) {
    const { pathname, searchParams } = new URL(request.url);

    if(request.method === 'GET' && pathname === '/version') {
      return new Response(env.CLIENT_VERSION);
    }

    if(request.method === 'GET' && pathname === '/info') {
      return Response.json({
        sources: env.SOURCES_URL,
        contact: env.CONTACT_EMAIL
      });
    }

    if(request.method === 'PUT' && pathname === '/videos') {
      try {
        const formData = await request.formData();
        const videoId = formData.get('video_id') as string | null;
        const userId = formData.get('user_id') as string | null;
        const language = formData.get('language') as string | null;
        const reason = formData.get('reason') as string | null;
        const file = formData.get('file') as File | null;
        if(!file) {
          return new Response('An error occurred during processing - no file', { status: 500 });
        }

        console.log('Video UUID:', videoId);
        console.log('File Name:', file.name);
        console.log('File Size:', file.size);

        if(file.size >= 1024 * 1024 * 12) {
          return new Response('Too large file', { status: 400 });
        }

        const ip = request.headers.get('cf-connecting-ip') ?? '';

        const objectKey = `${userId}_${videoId}_${language}_${reason}.webm`;
        console.log({ objectKey, videoId, userId, language, reason, ip });

        console.log(`Adding to database...`);
        const response = await env.DB.prepare('INSERT INTO videos (video_id, user_id, language, reason, object, available, ip, timestamp) VALUES (?, ?, ?, ?, ?, true, ?, CURRENT_TIMESTAMP)')
          .bind(videoId, userId, language, reason, objectKey, ip)
          .run();

        console.log(`Adding to object storage...`);
        await env.STORAGE.put(objectKey, file.slice());

        return Response.json(response);
      } catch(error) {
        console.error('Error:', error);
        return new Response('An error occurred during processing', { status: 500 });
      }
    }

    if(request.method === 'GET' && pathname === '/video') {
      let { seq }: SeqResponse = (await env.DB.prepare('SELECT seq FROM sqlite_sequence WHERE name = "videos"').first())!;

      const id = Math.floor(Math.random() * seq) + 1;
      const reason = Math.random() < 0.75 ? 'death' : 'extract';

      let result: VideoResponse | null = await env.DB.prepare('SELECT * FROM videos WHERE id >= ? AND available = 1 AND reason = ? LIMIT 1')
        .bind(id, reason)
        .first();

      // No with set reason?
      if(!result) {
        result = await env.DB.prepare('SELECT * FROM videos WHERE id >= ? AND available = 1 LIMIT 1')
          .bind(id)
          .first();
      }

      if(!result) {
        // Nothing we can do
        return new Response(
          `No videos, please report this as a bug. id >= ${id}, seq = ${seq}, reason = ${reason}`
        );
      }

      const object = await env.STORAGE.get(result.object);
      if(object === null) {
        return new Response('Object Not Found', { status: 404 });
      }

      const headers = new Headers();
      object.writeHttpMetadata(headers);
      headers.set('etag', object.httpEtag);

      return new Response(object.body, {
        headers
      });
    }

    return new Response('Not found');
  }
};
