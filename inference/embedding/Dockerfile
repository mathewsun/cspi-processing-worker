FROM python:3.12-slim-bookworm@sha256:e697c81897302a4c4333bade000d98038fbb21eb064f8ee1fd42d02ebcd9d337
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 OMP_NUM_THREADS=2 TOKENIZERS_PARALLELISM=false
WORKDIR /service
COPY requirements.lock ./
RUN pip install --no-cache-dir -r requirements.lock
COPY download.py constants.py ./
RUN python download.py
COPY server.py ./
USER 10001:10001
EXPOSE 8080
CMD ["uvicorn","server:app","--host","0.0.0.0","--port","8080","--workers","1","--limit-concurrency","8","--no-access-log"]
