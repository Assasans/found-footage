import { AwsClient } from 'aws4fetch';

const r2 = new AwsClient({
  accessKeyId: '1d87156207184c11fd2b7ac589c70643',
  secretAccessKey: '039ab2c94988bb0e3759a5b858761aad2026250e7959a39808db38446030359a'
});

export interface Env {
  SOURCES_URL: string;
  CONTACT_EMAIL: string;
  CLIENT_VERSION: string;
  LOKI: { URL: string, AUTH: string } | undefined;

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

function normalizePathname(pathname: string) {
  // Remove trailing slash
  if(pathname.charAt(pathname.length - 1) === '/') {
    pathname = pathname.slice(0, -1);
  }

  // Remove consecutive slashes
  pathname = pathname.replace(/\/\/+/g, '/');

  // Ensure leading slash
  if(pathname.charAt(0) !== '/') {
    pathname = '/' + pathname;
  }

  return pathname;
}

type LogEntry = {
  level: string,
  timestamp: string,
  action: string
};

const LOGS: LogEntry[] = [];
function log(level: string, properties: Record<string, unknown>) {
  const timestamp = (BigInt(performance.now()) * 1000000n).toString();
  LOGS.push({ level, timestamp, action: JSON.stringify(properties) });
}

async function sendLogs(env: Env) {
  // Nothing to do
  if(!env.LOKI) return;

  const body = {
    streams: LOGS.map(log => ({
      stream: {
        job: 'cloudflare',
        level: log.level
      },
      values: [[log.timestamp, log.action]]
    }))
  };
  LOGS.length = 0;

  console.log('Sendings logs', JSON.stringify(body));
  const response = await fetch(env.LOKI.URL, {
    method: 'POST',
    body: JSON.stringify(body),
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Basic ${btoa(env.LOKI.AUTH)}`
    }
  })
  console.log(response.status, await response.text());
}

function respond(ray: string, response: Response): Response {
  log('DEBUG', {
    action: 'response',
    ray,
    status: response.status,
    status_text: response.statusText
  });
  return response;
}

export default {
  async fetch(request: Request, env: Env) {
    try {
      const ip = request.headers.get('cf-connecting-ip') ?? '';
      const ray = request.headers.get('cf-ray') ?? '';

      let { pathname, searchParams } = new URL(request.url);
      pathname = normalizePathname(pathname);

      log('DEBUG', {
        action: 'processing request',
        ray: ray,
        method: request.method,
        path: pathname,
        host: request.headers.get('host'),
        ip: ip.length > 0 ? ip : null
      });

      log('TRACE', {
        action: 'trace request',
        ray: ray,
        headers: [...request.headers.entries()]
      });

      if(request.method === 'GET' && pathname === '/version') {
        log('TRACE', {
          action: 'get version',
          ray: ray,
          local: searchParams.get('local')
        });
        return respond(ray, new Response(env.CLIENT_VERSION));
      }

      if(request.method === 'GET' && pathname === '/info') {
        return respond(ray,Response.json({
          sources: env.SOURCES_URL,
          contact: env.CONTACT_EMAIL
        }));
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
            console.error(`No file uploaded`);
            log('INFO', {
              action: 'no file uploaded',
              ray: ray,
              method: request.method,
              path: pathname,
              ip: ip.length > 0 ? ip : null
            });
            return respond(ray, new Response('An error occurred during processing - no file', { status: 400 }))
          }

          console.log('Video UUID:', videoId);
          console.log('File Name:', file.name);
          console.log('File Size:', file.size);

          const MAX_SIZE = 1024 * 1024 * 12;
          if(file.size >= MAX_SIZE) {
            console.error(`Too large file: ${file.size / 1024 / 1024} MB > 12 MB`);
            log('INFO', {
              action: 'too large file uploaded',
              ray: ray,
              size: file.size,
              max_size: MAX_SIZE,
              ip: ip.length > 0 ? ip : null
            });
            return respond(ray, new Response('Too large file', { status: 400 }));
          }

          const objectKey = `${userId}_${videoId}_${language}_${reason}.webm`;
          console.log({ objectKey, videoId, userId, language, reason, ip });

          let response;
          try {
            console.log(`Adding to database...`);
            log('DEBUG', {
              action: 'adding to database',
              ray: ray,
              object: objectKey,
              video_id: videoId,
              user_id: userId,
              language,
              reason,
              ip
            });

            response = await env.DB.prepare('INSERT INTO videos (video_id, user_id, language, reason, object, available, ip, timestamp) VALUES (?, ?, ?, ?, ?, true, ?, CURRENT_TIMESTAMP)')
              .bind(videoId, userId, language, reason, objectKey, ip)
              .run();
          } catch(error) {
            console.error('Database error', error);
            // TypeScript 😊
            if(
              error &&
              typeof error === 'object' &&
              'message' in error &&
              typeof error.message === 'string'
            ) {
              if(error.message.includes('UNIQUE constraint failed: videos.video_id')) {
                log('INFO', {
                  action: 'duplicate file',
                  ray: ray,
                  video_id: videoId
                });
                return respond(ray, new Response(`Duplicate video ${videoId}`, { status: 400 }));
              }
            }
            throw error;
          }

          console.log(`Adding to object storage...`);
          log('DEBUG', {
            action: 'adding to object storage',
            ray: ray,
            object: objectKey,
            size: file.size
          });

          await env.STORAGE.put(objectKey, file.slice());

          return respond(ray, Response.json(response));
        } catch(error) {
          console.error('Error:', error);
          log('ERROR', {
            action: 'add video error',
            ray: ray,
            error: error instanceof Error ? error.toString() : error
          });

          return respond(ray, new Response('An error occurred during processing', { status: 500 }));
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
          return respond(ray, new Response(`No videos, please report this as a bug. id >= ${id}, seq = ${seq}, reason = ${reason}`));
        }

        const object = await env.STORAGE.get(result.object);
        if(object === null) {
          log('ERROR', {
            action: 'object not found',
            ray,
            object: result.object,
            video: result
          });
          return respond(ray, new Response('Object Not Found', { status: 404 }));
        }

        const headers = new Headers();
        object.writeHttpMetadata(headers);
        headers.set('etag', object.httpEtag);

        return respond(ray, new Response(object.body, {
          headers
        }));
      }

      if(request.method === 'GET' && pathname === '/video/signed') {
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
          return respond(ray, new Response(`No videos, please report this as a bug. id >= ${id}, seq = ${seq}, reason = ${reason}`));
        }

        const object = await env.STORAGE.get(result.object);
        if(object === null) {
          log('ERROR', {
            action: 'object not found',
            ray,
            object: result.object,
            video: result
          });
          return respond(ray, new Response('Object Not Found', { status: 404 }));
        }

        const bucketName = 'prod-foundfootage';
        const accountId = '1e6ab2b283478194e3297fac143759e9';
    
        // const url = new URL('https://1e6ab2b283478194e3297fac143759e9.r2.cloudflarestorage.com');
        const url = new URL(`https://${bucketName}.${accountId}.r2.cloudflarestorage.com`);
    
        url.pathname = result.object;
        // Specify a custom expiry for the presigned URL, in seconds
        url.searchParams.set('X-Amz-Expires', '3600');
    
        const signed = await r2.sign(
          new Request(url, {
            method: 'GET',
          }),
          {
            aws: { signQuery: true },
          }
        );
    
        // Caller can now use this URL to upload to that object.
        return respond(ray, new Response(signed.url, { status: 200 }));
      }

      log('INFO', {
        action: 'route not found',
        ray: ray,
        method: request.method,
        path: pathname,
        ip: ip.length > 0 ? ip : null
      });
      return respond(ray, new Response('Not found', { status: 404 }));
    } finally {
      await sendLogs(env);
    }
  }
};
