import { AwsClient } from 'aws4fetch';

type RatelimitConfig = { TIMEFRAME: string; THRESHOLD: string; BAN_THRESHOLD: string };

export interface Env {
  SOURCES_URL: string;
  CONTACT_EMAIL: string;
  CLIENT_VERSION: string;
  R2_SIGNING: { ACCESS_KEY_ID: string, SECRET_ACCESS_KEY: string, CLOUDFLARE_ACCOUNT: string, BUCKET: string };
  LOKI: { URL: string, AUTH: string } | undefined;
  CLOUDFLARE_AUTH: { EMAIL: string, KEY: string };
  RATELIMITS: { ACCESS: RatelimitConfig; NO_ROUTE: RatelimitConfig };

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
  position: string;
  content_buffer: string;
}

interface RatelimitResponse {
  ip: string;
  asn: string;
  timestamp: string;
  type: string;
  count: number;
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

interface GetSignedVideoRequest {
  day: number | null;
  playerCount: number | null;
  reason: string | null;
  language: string | null;
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

  // console.log('Sendings logs', JSON.stringify(body));
  const response = await fetch(env.LOKI.URL, {
    method: 'POST',
    body: JSON.stringify(body),
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Basic ${btoa(env.LOKI.AUTH)}`
    }
  })
  // console.log(response.status, await response.text());
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

async function blockIp(env: Env, result: RatelimitResponse, request: Request, pathname: string, ray: string, ip: string) {
  const response = await fetch(`https://api.cloudflare.com/client/v4/accounts/${env.R2_SIGNING.CLOUDFLARE_ACCOUNT}/firewall/access_rules/rules`, {
    headers: {
      'X-Auth-Email': env.CLOUDFLARE_AUTH.EMAIL,
      'X-Auth-Key': env.CLOUDFLARE_AUTH.KEY,
      'Content-Type': 'application/json',
      'X-Cross-Site-Security': 'dash'
    },
    method: 'POST',
    body: JSON.stringify({
      id: '',
      configuration: {
        target: 'ip',
        value: ip
      },
      mode: 'block',
      notes: `(auto) Ratelimit reached (requests: ${result.count}, type: ${result.type}, ASN: ${request.cf?.asn})`,
      scope: {
        type: 'account'
      }
    }),
  });

  log('INFO', {
    action: 'secondary rate limit exceeded',
    ray: ray,
    method: request.method,
    path: pathname,
    host: request.headers.get('host'),
    type: result.type,
    count: result.count,
    block_response: response.status,
    ip: ip.length > 0 ? ip : null
  });

  if(response.ok) {
    const data = await response.text();
    log('ERROR', {
      action: 'block failed',
      ray: ray,
      block_response: data,
      ip: ip.length > 0 ? ip : null
    });
  }
}

async function getRateLimit(env: Env, request: Request, config: RatelimitConfig, ip: string, type: string): Promise<RatelimitResponse | null> {
  const QUERY = `
    SELECT * FROM ratelimits WHERE ip = ? AND strftime('%s', CURRENT_TIMESTAMP) - strftime('%s', timestamp) <= ${Number(config.TIMEFRAME)} AND type = ?
  `;

  return await env.DB.prepare(QUERY)
    .bind(ip, type)
    .first();
}

async function incrementRateLimit(env: Env, request: Request, config: RatelimitConfig, ip: string, type: string): Promise<RatelimitResponse> {
  const QUERY = `
    INSERT INTO ratelimits (ip, asn, timestamp, type, count)
    VALUES (?, ?, CURRENT_TIMESTAMP, ?, 1)
    ON CONFLICT (ip) DO UPDATE
    SET
      timestamp = CURRENT_TIMESTAMP,
      count = CASE
        WHEN strftime('%s', CURRENT_TIMESTAMP) - strftime('%s', timestamp) <= ${Number(config.TIMEFRAME)} THEN count + 1
        ELSE 1
      END
    RETURNING *
  `;

  const response = await env.DB.prepare(QUERY)
    .bind(ip, String(request.cf?.asn), type)
    .run();

  // @ts-ignore there is
  const result: RatelimitResponse = response.results[0];
  return result;
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
        headers: [...request.headers.entries()],
        ip: ip.length > 0 ? ip : null
      });

      {
        const ratelimit = await incrementRateLimit(env, request, env.RATELIMITS.ACCESS, ip, 'access');
        if(ratelimit.count > Number(env.RATELIMITS.ACCESS.BAN_THRESHOLD)) {
          await blockIp(env, ratelimit, request, pathname, ray, ip);
          return respond(ray, Response.json('You are getting permanently banned...', { status: 429, statusText: 'Too Many Requests' }));
        }

        if(ratelimit.count > Number(env.RATELIMITS.ACCESS.THRESHOLD)) {
          log('DEBUG', {
            action: 'primary rate limit exceeded',
            ray: ray,
            method: request.method,
            path: pathname,
            host: request.headers.get('host'),
            type: ratelimit.type,
            count: ratelimit.count,
            ip: ip.length > 0 ? ip : null
          });
          return respond(ray, Response.json('Shut the fuck up (access)', { status: 429, statusText: 'Too Many Requests' }));
        }
      }

      {
        const ratelimit = await getRateLimit(env, request, env.RATELIMITS.NO_ROUTE, ip, 'no-route');
        if(ratelimit && ratelimit.count > Number(env.RATELIMITS.NO_ROUTE.THRESHOLD)) {
          log('DEBUG', {
            action: 'primary rate limit exceeded (early return)',
            ray: ray,
            method: request.method,
            path: pathname,
            host: request.headers.get('host'),
            type: ratelimit.type,
            count: ratelimit.count,
            ip: ip.length > 0 ? ip : null
          });
          return respond(ray, Response.json('Shut the fuck up (no route early)', { status: 429, statusText: 'Too Many Requests' }));
        }
      }

      if(pathname === '/favicon.ico') {
        return respond(ray, new Response(null, { status: 204, statusText: 'No Content' }));
      }

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
          const lobbyId = formData.get('lobby_id') as string | null;
          const language = formData.get('language') as string | null;
          const reason = formData.get('reason') as string | null;
          let position = formData.get('position') as string | null;
          const version = formData.get('version') as string | null;
          const day = formData.get('day') as string | null;
          const contentBuffer = formData.get('content_buffer') as string | null;
          const playerCount = formData.get('player_count') as string | null;
          const secretUserId = formData.get('secret_user_id') as string | null;
          const file = formData.get('file') as File | null;
          if(!version && reason === 'extract' && Math.random() < 0.9) {
            console.log('Reject randomly');
            log('INFO', {
              action: 'reject randomly',
              ray: ray,
              video_id: videoId,
              user_id: userId,
              lobby_id: lobbyId,
              language,
              reason,
              ip: ip.length > 0 ? ip : null
            });
            return respond(ray, new Response('Rejected randomly', { status: 400 }));
          }

          // Bruh
          // console.log('tes123t', { aanb: version, cc: position, dd: day, aa: contentBuffer });
          // if(version !== null) return respond(ray, new Response('TEST', { status: 400 }));

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

          if(!position || position.length < 1 || position === 'null') position = null;

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

          const areEqual = (first: Uint8Array, second: Uint8Array) => first.length === second.length && first.every((value, index) => value === second[index]);

          const fileContent = await file.arrayBuffer();
          const signature = new Uint8Array(fileContent.slice(0, 4));
          const MATROSKA_SIGNATURE = Uint8Array.from([0x1A, 0x45, 0xDF, 0xA3]);
          if(!areEqual(signature, MATROSKA_SIGNATURE)) {
            console.error(`Invalid file type`);
            log('INFO', {
              action: 'invalid file type uploaded',
              ray: ray,
              name: file.name,
              ip: ip.length > 0 ? ip : null
            });
            return respond(ray, new Response('Invalid file type', { status: 400 }));
          }

          const objectKey = `${userId}_${videoId}_${language}_${reason}.webm`;
          console.log({ objectKey, videoId, userId, lobbyId, language, reason, version, day, position, secretUserId, playerCount, ip });

          let response;
          try {
            console.log(`Adding to database...`);
            log('DEBUG', {
              action: 'adding to database',
              ray: ray,
              object: objectKey,
              video_id: videoId,
              user_id: userId,
              lobby_id: lobbyId,
              language,
              reason,
              version,
              day,
              position,
              content_buffer: contentBuffer,
              secret_user_id: secretUserId,
              player_count: playerCount,
              ip
            });

            response = await env.DB.prepare('INSERT INTO videos (video_id, user_id, language, reason, object, available, ip, timestamp, lobby_id, version, day, position, content_buffer, secret_user_id, player_count) VALUES (?, ?, ?, ?, ?, true, ?, CURRENT_TIMESTAMP, ?, ?, ?, ?, ?, ?, ?)')
              .bind(videoId, userId, language, reason, objectKey, ip, lobbyId, version, day, position?.length ? position : null, contentBuffer, secretUserId, playerCount)
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

          await env.STORAGE.put(objectKey, fileContent);

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

          // Dangling database entry
          const response = await env.DB.prepare('UPDATE videos SET available = 0 WHERE id = ?')
            .bind(id)
            .run();
          log('INFO', {
            action: 'hide dangling video',
            ray,
            video: result,
            response: response
          });

          return respond(ray, Response.redirect('/video?dangling=1', 302));
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

          // Dangling database entry
          const response = await env.DB.prepare('UPDATE videos SET available = 0 WHERE id = ?')
            .bind(id)
            .run();
          log('INFO', {
            action: 'hide dangling video',
            ray,
            video: result,
            response: response
          });

          return respond(ray, Response.redirect('/video/signed?dangling=1', 302));
        }

        const r2 = new AwsClient({
          accessKeyId: env.R2_SIGNING.ACCESS_KEY_ID,
          secretAccessKey: env.R2_SIGNING.SECRET_ACCESS_KEY
        });

        const url = new URL(`https://${env.R2_SIGNING.BUCKET}.${env.R2_SIGNING.CLOUDFLARE_ACCOUNT}.r2.cloudflarestorage.com`);

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
        return respond(ray, new Response(signed.url, {
          status: 200,
          headers: {
            'X-Video-Id': result.video_id
          }
        }));
      }

      if(request.method === 'POST' && pathname === '/v3/video/signed') {
        let { seq }: SeqResponse = (await env.DB.prepare('SELECT seq FROM sqlite_sequence WHERE name = "videos"').first())!;

        const params = await request.json<GetSignedVideoRequest>();
        if(![null, 'death', 'extract'].includes(params.reason)) params.reason = null;

        const id = Math.floor(Math.random() * seq) + 1;
        const reason = Math.random() < 0.75 ? 'death' : 'extract';

        let result: VideoResponse | null = null;
        
        if(params.day) {
          result = await env.DB.prepare('SELECT * FROM videos WHERE id >= ? AND available = 1 AND reason = ? AND day = ? AND content_buffer IS NOT NULL LIMIT 1')
            .bind(id, reason, params.day)
            .first();
        } else {
          result = await env.DB.prepare('SELECT * FROM videos WHERE id >= ? AND available = 1 AND reason = ? AND content_buffer IS NOT NULL LIMIT 1')
            .bind(id, reason)
            .first();
        }

        // Fallback to any video
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

          // Dangling database entry
          const response = await env.DB.prepare('UPDATE videos SET available = 0 WHERE id = ?')
            .bind(id)
            .run();
          log('INFO', {
            action: 'hide dangling video',
            ray,
            video: result,
            response: response
          });

          return respond(ray, Response.redirect('/video/signed?dangling=1', 302));
        }

        const r2 = new AwsClient({
          accessKeyId: env.R2_SIGNING.ACCESS_KEY_ID,
          secretAccessKey: env.R2_SIGNING.SECRET_ACCESS_KEY
        });

        const url = new URL(`https://${env.R2_SIGNING.BUCKET}.${env.R2_SIGNING.CLOUDFLARE_ACCOUNT}.r2.cloudflarestorage.com`);

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
        return respond(ray, Response.json({
          url: signed.url,
          videoId: result.video_id,
          position: result.position,
          contentBuffer: result.content_buffer.length > 0 ? result.content_buffer : null
        }));
      }

      if(request.method === 'POST' && pathname === '/video/vote') {
        try {
          const formData = await request.formData();
          const videoId = formData.get('video_id') as string | null;
          const userId = formData.get('user_id') as string | null;
          const lobbyId = formData.get('lobby_id') as string | null;
          const voteType = formData.get('vote_type') as string | null;

          console.log({ videoId, userId, lobbyId, voteType });

          let response;
          try {
            console.log(`Adding vote to database...`);
            log('DEBUG', {
              action: 'adding vote to database',
              ray: ray,
              video_id: videoId,
              user_id: userId,
              lobby_id: lobbyId,
              vote_type: voteType,
              ip
            });

            response = await env.DB.prepare('INSERT INTO votes (video_id, user_id, lobby_id, vote_type, flags, ip, timestamp) VALUES (?, ?, ?, ?, 1, ?, CURRENT_TIMESTAMP)')
              .bind(videoId, userId, lobbyId, voteType, ip)
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
              if(error.message.includes('UNIQUE constraint failed: votes.video_id, votes.user_id')) {
                log('INFO', {
                  action: 'duplicate vote',
                  ray: ray,
                  video_id: videoId,
                  user_id: userId
                });
                return respond(ray, new Response(`Duplicate vote ${videoId} & ${userId}`, { status: 400 }));
              }
            }
            throw error;
          }

          return respond(ray, Response.json(response));
        } catch(error) {
          console.error('Error:', error);
          log('ERROR', {
            action: 'add vote error',
            ray: ray,
            error: error instanceof Error ? error.toString() : error
          });

          return respond(ray, new Response('An error occurred during processing', { status: 500 }));
        }
      }

      log('INFO', {
        action: 'route not found',
        ray: ray,
        method: request.method,
        path: pathname,
        ip: ip.length > 0 ? ip : null
      });

      {
        const ratelimit = await incrementRateLimit(env, request, env.RATELIMITS.NO_ROUTE, ip, 'no-route');
        if(ratelimit.count > Number(env.RATELIMITS.NO_ROUTE.BAN_THRESHOLD)) {
          await blockIp(env, ratelimit, request, pathname, ray, ip);
          return respond(ray, Response.json('You are getting permanently banned...', { status: 429, statusText: 'Too Many Requests' }));
        }

        if(ratelimit.count > Number(env.RATELIMITS.NO_ROUTE.THRESHOLD)) {
          log('DEBUG', {
            action: 'primary rate limit exceeded',
            ray: ray,
            method: request.method,
            path: pathname,
            host: request.headers.get('host'),
            type: ratelimit.type,
            count: ratelimit.count,
            ip: ip.length > 0 ? ip : null
          });
          return respond(ray, Response.json('Shut the fuck up (no route)', { status: 429, statusText: 'Too Many Requests' }));
        }
      }

      return respond(ray, new Response('Not found', { status: 404 }));
    } catch(error) {
      console.error('unexpected error', error);
      log('ERROR', {
        action: 'unexpected error',
        error: error instanceof Error ? error.toString() : error
      });
    } finally {
      await sendLogs(env);
    }
  }
};
