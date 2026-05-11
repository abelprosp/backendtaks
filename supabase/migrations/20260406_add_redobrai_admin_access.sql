insert into public.user_role (user_id, role_id)
select u.id, r.id
from public."User" u
join public."Role" r on r.slug = 'admin'
where lower(u.email) = 'redobrai@gmail.com'
  and not exists (
    select 1
    from public.user_role ur
    where ur.user_id = u.id
      and ur.role_id = r.id
  );
