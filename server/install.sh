#!/usr/bin/env bash
# Run the exported script as root. Do not run unreviewed downloaded scripts.
set -euo pipefail
: "${RPF_USER:?Set RPF_USER to the existing Linux account}"
: "${RPF_ROOT:?Set RPF_ROOT to a dedicated project directory}"
: "${RPF_DEVICE:?Set RPF_DEVICE to the Windows Syncthing device ID}"
: "${RPF_FOLDER:=remote-project}"
export RPF_USER RPF_ROOT RPF_DEVICE RPF_FOLDER
if [ "$(id -u)" != 0 ]; then echo '请以 root 执行此脚本。'; exit 1; fi
if [ "$(uname -m)" != x86_64 ]; then echo '此发行包支持 x86_64 Ubuntu。'; exit 1; fi
command -v python3 >/dev/null || { echo '需要 python3'; exit 1; }
python3 - <<'PY'
import os,pwd,re,xml.etree.ElementTree as E
u=os.environ['RPF_USER']; p=os.environ['RPF_ROOT']
if not re.fullmatch(r'[a-z_][a-z0-9_-]*\$?',u): raise SystemExit('无效用户名')
a=pwd.getpwnam(u)
if a.pw_uid==0: raise SystemExit('请使用普通账号运行同步服务')
if not os.path.isabs(p) or '..' in p.split('/') or any(ord(c)<32 for c in p): raise SystemExit('无效项目目录')
if os.path.normpath(p) in ('/','/home','/root',a.pw_dir): raise SystemExit('请选择专用项目子目录')
if os.path.islink(p): raise SystemExit('项目目录不能是符号链接')
if not re.fullmatch(r'[A-Z2-7]{7}(?:-[A-Z2-7]{7}){7}',os.environ['RPF_DEVICE']): raise SystemExit('无效设备 ID')
if not re.fullmatch(r'[a-zA-Z0-9_-]{1,64}',os.environ['RPF_FOLDER']): raise SystemExit('无效目录 ID')
config=a.pw_dir+'/.local/state/remote-project-sync/config.xml'
if os.path.exists(config):
    for folder in E.parse(config).findall('folder'):
        if folder.get('id')==os.environ['RPF_FOLDER'] and os.path.normpath(folder.get('path',''))!=os.path.normpath(p):
            raise SystemExit('已有同步 ID 绑定另一目录；保持现有服务运行，不执行迁移')
PY
apt-get update
apt-get install -y curl ca-certificates tmux python3
install -d -m 755 /usr/local/lib/remote-project-folder
tmp=$(mktemp -d)
trap 'rm -rf -- "$tmp"' EXIT
archive=syncthing-linux-amd64-v2.1.7.tar.gz
curl --fail --location --retry 3 --output "$tmp/$archive" "https://github.com/syncthing/syncthing/releases/download/v2.1.7/$archive"
printf '%s  %s\n' '2edc416e7ed136b7c858ed5f3f02a3b8316589605e54c380228ceea7acbf0142' "$tmp/$archive" | sha256sum -c -
tar -xzf "$tmp/$archive" -C "$tmp"
install -m 755 "$tmp/syncthing-linux-amd64-v2.1.7/syncthing" /usr/local/lib/remote-project-folder/syncthing.new
mv -f /usr/local/lib/remote-project-folder/syncthing.new /usr/local/lib/remote-project-folder/syncthing
home=$(getent passwd "$RPF_USER" | cut -d: -f6)
state="$home/.local/state/remote-project-sync"
service="remote-project-sync-$RPF_USER.service"
if systemctl is-active --quiet "$service"; then systemctl stop "$service"; fi
runuser -u "$RPF_USER" -- mkdir -p -- "$state" "$RPF_ROOT"
runuser -u "$RPF_USER" -- test -w "$RPF_ROOT" || { echo '项目目录不可写；脚本不会递归修改已有文件所有权。'; exit 1; }
if [ ! -f "$state/config.xml" ]; then
    runuser -u "$RPF_USER" -- /usr/local/lib/remote-project-folder/syncthing generate --home="$state"
fi
# Configure only our dedicated Syncthing instance. Keep the original backup.
python3 - <<'PY'
import os,pwd,shutil,xml.etree.ElementTree as E
u=os.environ['RPF_USER']; account=pwd.getpwnam(u)
home=account.pw_dir; root=os.environ['RPF_ROOT']; peer=os.environ['RPF_DEVICE']; fid=os.environ['RPF_FOLDER']
state=home+'/.local/state/remote-project-sync'; file=state+'/config.xml'
tree=E.parse(file); c=tree.getroot()
if not os.path.exists(file+'.before-rpf'): shutil.copy2(file,file+'.before-rpf')
for f in c.findall('folder'):
    if f.get('id')==fid and os.path.normpath(f.get('path',''))!=os.path.normpath(root): raise SystemExit('已有同步 ID 绑定不同目录，停止以防误同步')
    c.remove(f)
for d in list(c.findall('device')):
    if d.get('id')==peer: c.remove(d)
d=E.SubElement(c,'device',{'id':peer,'name':'Windows workbench','compression':'metadata','introducer':'false'})
E.SubElement(d,'address').text='dynamic'
gui=c.find('gui'); gui.set('enabled','true');gui.set('tls','false');gui.find('address').text='127.0.0.1:18385'
options=c.find('options')
def setopt(key,value):
    ns=options.findall(key)
    for n in ns: options.remove(n)
    E.SubElement(options,key).text=value
setopt('listenAddress','tcp://0.0.0.0:22020')
for key in ('globalAnnounceEnabled','localAnnounceEnabled','relaysEnabled','natEnabled','startBrowser'): setopt(key,'false')
setopt('urAccepted','-1');setopt('autoUpgradeIntervalH','0')
f=E.SubElement(c,'folder',{'id':fid,'label':'Remote project','path':root,'type':'sendreceive','rescanIntervalS':'60','fsWatcherEnabled':'true','fsWatcherDelayS':'2','ignorePerms':'true'})
E.SubElement(f,'filesystemType').text='basic'
own=c.find('device').get('id')
E.SubElement(f,'device',{'id':own});E.SubElement(f,'device',{'id':peer})
v=E.SubElement(f,'versioning',{'type':'simple'});E.SubElement(v,'param',{'key':'keep','val':'20'});E.SubElement(v,'param',{'key':'cleanoutDays','val':'0'})
ignore='.git\nnode_modules\n.venv\nvenv\n__pycache__\n.next\n.cache\n.env\n.env.*\n.ssh\n.claude\n.codex\n*.pem\n*.key\n*.p12\n*.pfx\n'
p=root+'/.stignore';old=open(p,encoding='utf-8').read() if os.path.exists(p) else ''
if not old.startswith(ignore):
    with open(p,'w',encoding='utf-8',newline='\n') as out:out.write(ignore+old)
os.chown(p,account.pw_uid,account.pw_gid)
tree.write(file,encoding='utf-8',xml_declaration=True);os.chown(file,account.pw_uid,account.pw_gid);os.chmod(file,0o600)
def unitquote(s):return '"'+s.replace('\\','\\\\').replace('"','\\"').replace('%','%%')+'"'
unit='[Unit]\nDescription=Remote Project Folder sync\nAfter=network-online.target\nWants=network-online.target\n\n[Service]\nUser='+u+'\nUMask=0077\nExecStart=/usr/local/lib/remote-project-folder/syncthing serve --home='+unitquote(state)+' --no-browser --no-upgrade --no-restart\nRestart=on-failure\nRestartSec=5\nNoNewPrivileges=true\n\n[Install]\nWantedBy=multi-user.target\n'
with open('/etc/systemd/system/remote-project-sync-'+u+'.service','w') as out:out.write(unit)
PY
systemctl daemon-reload
systemctl enable --now "$service"
sleep 2
systemctl is-active --quiet "$service"
printf '\n服务器设备 ID（复制到工作台配置）：\n'
runuser -u "$RPF_USER" -- /usr/local/lib/remote-project-folder/syncthing device-id --home="$state"
printf '\n配对目录：%s\n请在云防火墙和启用的主机防火墙放行 TCP 22020。不要开放 18385 管理端口。\n' "$RPF_ROOT"
printf '服务检查：systemctl status %s\n' "$service"
printf '停止服务：systemctl disable --now %s\n' "$service"
